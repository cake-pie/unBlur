using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEngine;

namespace UnBlur
{
    [KSPAddon(KSPAddon.Startup.Instantly, true)]
    public sealed class UnBlur : LoadingSystem
    {
        #region Lifecycle
        public static UnBlur Instance;

        private void Awake()
        {
            if (LoadingScreen.Instance == null || Instance != null)
            {
                Destroy(gameObject);
                return;
            }
            Instance = this;
            DontDestroyOnLoad(gameObject);

            // insert ourselves at the end of the list of LoadingSystems
            // this ensures game does not go LOADING->MAINMENU before we are done with batch processing
            // if ModuleManager is installed, it would either already be ahead of us in the list, or
            // would insert immediately after GameDatabase (and ahead of us) when it adds itself later
            // in any case, we don't actually use StartLoad() but instead begin as soon as GameDatabase
            // and ModuleManager (if installed) have completed their loading
            Log("Adding self to loading screen");
            LoadingScreen.Instance.loaders.Add(this);

            // GameDatabase will tell us when it has finished loading; we will check ModuleManager ourselves
            // Note that subsequent database reloads after LOADING will not reload textures, but we run anyway
            // This will service any new de-mipmap entries in modified configs, but cannot help if existing code
            // continues to hang on to an older, mipmapped copy of the texture that it obtained previously
            // Also does not undo any textures which are already de-mipmapped
            GameEvents.OnGameDatabaseLoaded.Add(Run);
        }

        private void OnDestroy()
        {
            GameEvents.OnGameDatabaseLoaded.Remove(Run);
        }
        #endregion Lifecycle

        #region LoadingSystem
        private bool ready = false;
        public override bool IsReady() { return ready; }

        public override float LoadWeight() { return 0; }
        public override float ProgressFraction() { return 1; }

        private string progressTitle = "unBlur";
        public override string ProgressTitle() { return progressTitle; }

        // We don't actually use this
        public override void StartLoad() {}
        #endregion LoadingSystem

        #region Batch Processing
        private const string CfgNodeName = "UNBLUR_BATCH";
        private const string CfgValTexture = "unBlurTexture";
        private const string CfgValFolderS = "unBlurFolder";
        private const string CfgValFolderR = "unBlurFolderRecursive";
        private const string CfgValCompress = "compress";
        private const string CfgNodeDebug = "UNBLUR_DEBUG";
        private const string CfgValVerbose = "verbose";

        private void Run()
        {
            ready = false;
            progressTitle = "unBlur: Waiting...";
            Log("Batch processing triggered");
            StartCoroutine(TestAndWaitForMM());
        }

        private IEnumerator TestAndWaitForMM()
        {
            while (!GameDatabase.Instance.IsReady()) yield return null;  // should never happen in theory
            GameDataDir = GameDatabase.Instance.root.children.FirstOrDefault(c => c.path.EndsWith("GameData"));

            // Read debugging settings (which do not support MM patching)
            ConfigNode[] debugNodes = GameDatabase.Instance.GetConfigNodes(CfgNodeDebug);
            debug = debugNodes.Length > 0;
            verbose = false;
            for (int i = 0; i < debugNodes.Length; i++)
                if (debugNodes[i].TryGetValue(CfgValVerbose, ref verbose) && verbose)
                    break;

            // When reloading via ModuleManager (Alt+F11) PostPatchLoader/MMPatchLoader's StartLoad() is only called
            // after GameDatabase is ready, so yield to let it set ready = false; and ensure we don't get ahead of it
            yield return null;

            progressTitle = "unBlur: Waiting for ModuleManager to finish patching GameDatabase";

            if (!MMAbsentOrReady())
            {
                if (mmPostLoad)
                {
                    Log("Waiting for ModuleManager to call ModuleManagerPostLoad()");
                    yield break;
                }
                Log("Waiting for ModuleManager: yielding until its LoadingSystem is ready");
            }

            while (!MMAbsentOrReady()) yield return null;

            BatchUnBlur();
        }

        // Called by MM v2.6.7+ when done
        public void ModuleManagerPostLoad()
        {
            BatchUnBlur();
        }

        private void BatchUnBlur()
        {
            progressTitle = "unBlur: Processing...";
            if (verbose) DumpTextureInfo();
            Log("#### BEGIN BATCH PROCESSING ####\n{");

            ConfigNode[] nodes = GameDatabase.Instance.GetConfigNodes(CfgNodeName);
            int nodeCount = nodes.Length;
            for (int nodeIdx = 0; nodeIdx < nodeCount; nodeIdx++)
            {
                bool compress = false;
                if (!nodes[nodeIdx].TryGetValue(CfgValCompress, ref compress)) compress = false;

                string[] tgtFR = nodes[nodeIdx].GetValues(CfgValFolderR);
                string[] tgtFS = nodes[nodeIdx].GetValues(CfgValFolderS);
                string[] tgtT  = nodes[nodeIdx].GetValues(CfgValTexture);
                int targetTotal = tgtFR.Length + tgtFS.Length + tgtT.Length;
                int targetCurr = 0;
                for (int i = 0; i < tgtFR.Length; i++)
                {
                    progressTitle = $"unBlur: Processing target {++targetCurr}/{targetTotal} in node {nodeIdx+1}/{nodeCount}";
                    DisableMipmapsInFolder(tgtFR[i], compress, true);
                }
                for (int i = 0; i < tgtFS.Length; i++)
                {
                    progressTitle = $"unBlur: Processing target {++targetCurr}/{targetTotal} in node {nodeIdx+1}/{nodeCount}";
                    DisableMipmapsInFolder(tgtFS[i], compress, false);
                }
                for (int i = 0; i < tgtT.Length; i++)
                {
                    progressTitle = $"unBlur: Processing target {++targetCurr}/{targetTotal} in node {nodeIdx+1}/{nodeCount}";
                    DisableMipmaps(tgtT[i], compress);
                }
            }

            Log("}\n#### END BATCH PROCESSING ####");
            if (verbose) DumpTextureInfo();
            progressTitle = "unBlur: Done!";
            ready = true;
        }
        #endregion Batch Processing

        #region Core
        private UrlDir GameDataDir;
        private bool debug = false;
        private bool verbose = false;

        public bool DisableMipmaps(string url, bool compress)
        {
            GameDatabase.TextureInfo texInfo = GameDatabase.Instance.GetTextureInfo(url);
            if (texInfo == null)
            {
                Log($"Unable to disable mipmaps for {url} -- texture not found");
                return false;
            }

            return DisableMipmaps(texInfo, compress);
        }

        private bool DisableMipmaps(GameDatabase.TextureInfo texInfo, bool compress)
        {
            if (texInfo.texture.mipmapCount <= 1)
            {
                Log($"No need to disable mipmaps for {texInfo.name} -- already has none");
                if (compress && !texInfo.isCompressed)
                {
                    if (debug)
                    {
                        Log("  BEFORE " + FormatTextureInfo(texInfo));
                        Log("  Compression attempted");
                    }
                    texInfo.texture.Compress(true);
                    texInfo.isCompressed = texInfo.texture.format.isDXT();
                    if (debug) Log("  AFTER  " + FormatTextureInfo(texInfo));
                }
                return true;
            }

            Log($"Disabling mipmaps on {texInfo.name}...");
            if (debug) Log("  BEFORE " + FormatTextureInfo(texInfo));

            Texture2D oldTex = texInfo.texture;
            TextureFormat newTexFmt = oldTex.format;
            if (newTexFmt.isDXT1())
                newTexFmt = TextureFormat.RGB24;
            else if (newTexFmt.isDXT5())
                newTexFmt = TextureFormat.ARGB32;
            if (!newTexFmt.canSetPixels())
            {
                // look into GetRawTextureData / LoadRawTextureData
                // needs further research and testing, as it requires exactly correctly-sized byte[]
                Log($"... unable to proceed: cannot handle {oldTex.format.ToString()} format");
                return false;
            }

            Texture2D newTex = new Texture2D(oldTex.width, oldTex.height, newTexFmt, false);
            bool unreadable = false;
            try {
                if (newTexFmt.canSetPixels32())
                    newTex.SetPixels32(oldTex.GetPixels32(0), 0);
                else
                    newTex.SetPixels(oldTex.GetPixels(0), 0);
            }
            catch (UnityException)
            {
                Log("  INFO: Texture is unreadable, using fallback technique");
                unreadable = true;
                RenderTexture bak = RenderTexture.active;
                RenderTexture tmp = RenderTexture.GetTemporary(oldTex.width, oldTex.height, 0);
                Graphics.Blit(oldTex, tmp);
                RenderTexture.active = tmp;
                newTex.ReadPixels(new Rect(0, 0, oldTex.width, oldTex.height), 0, 0);
                RenderTexture.active = bak;
                RenderTexture.ReleaseTemporary(tmp);
            }
            if (unreadable == texInfo.isReadable)
                Log("  WARN: GameDatabase TextureInfo has incorrect readable status");
            if (compress || oldTex.format.isDXT())
            {
                newTex.Compress(true);
                texInfo.isCompressed = newTex.format.isDXT();
                if (debug) Log("  Compression attempted");
            }
            newTex.Apply(false, unreadable);
            texInfo.texture = newTex;
            Destroy(oldTex);
            oldTex = null;

            if (debug) Log("  AFTER  " + FormatTextureInfo(texInfo));
            Log("... done");
            return true;
        }

        private bool DisableMipmapsInFolder(string url, bool compress, bool recursive = false)
        {
            if (!GameDataDir.DirectoryExists(url))
            {
                Log($"Unable to disable mipmaps in folder {url} -- folder does not exist");
                return false;
            }

            bool success = true;
            List<GameDatabase.TextureInfo> texInfos = GameDatabase.Instance.GetAllTexturesInFolder(url);
            for (int i = 0; i < texInfos.Count; i++)
                success = DisableMipmaps(texInfos[i], compress) && success;

            if (recursive)
            {
                List<UrlDir> children = GameDataDir.GetDirectory(url).children;
                for (int i = 0; i < children.Count; i++)
                    success = DisableMipmapsInFolder(children[i].url+"/", compress, true) && success;
            }

            return success;
        }

        private void DumpTextureInfo()
        {
            StringBuilder sb = new StringBuilder("Dumping texture info from GameDatabase\n");
            sb.Append("#### BEGIN TEXTURE INFO DUMP ####\n{\n");
            List<GameDatabase.TextureInfo> texInfos = GameDatabase.Instance.databaseTexture;
            for (int i = 0; i < texInfos.Count; i++)
                sb.Append(FormatTextureInfo(texInfos[i]) + "\n");
            sb.Append("}\n#### END TEXTURE INFO DUMP ####");

            Log(sb.ToStringAndRelease());
        }

        private string FormatTextureInfo(GameDatabase.TextureInfo texInfo)
        {
            return
                (texInfo.isNormalMap  ? "N" : " ") +
                (texInfo.isReadable   ? "R" : " ") +
                (texInfo.isCompressed ? "C" : "-") +
                (texInfo.texture.format.isDXT() ? "C" : "-") +
                String.Format(" {0,4:D}x{1,-4:D} {2,-2:D} ", texInfo.texture.width, texInfo.texture.height, texInfo.texture.mipmapCount) +
                texInfo.texture.format.ToString("G").PadRight(16) +
                texInfo.name;
        }
        #endregion Core

        // TODO
        #region Debug Console
/*
        private const string DbgCommand = "unBlur";
        private const string DbgHelpString = "unBlur console tool.";

        private const string DbgCmdHelp = "help";
        private const string DbgCmdTex  = "txtr";
        private const string DbgCmdInfo = "info";
        private const string DbgCmdDump = "dump";

        private void DbgOnCommand(string argStr)
        {
            Log("Not implemented yet.");
        }
*/
        #endregion Debug Console

        #region MM Compatibility
        // ModuleManager is helpful and will call ModuleManagerPostLoad() after it is done
        // - since MM 2.6.7 (Aug 4, 2015)
        // If we use that naively, then we must either:
        // - rely on it solely, ignoring stock GameDatabase-only reloads (makes MM a dependency) or
        // - run BatchUnBlur for both OnGameDatabaseLoaded and ModuleManagerPostLoad
        // Neither is ideal, so instead run our own check if MM is present / which version
        // and then selectively decide what to do from there

        // Full notes from research on MM
        // https://gist.github.com/cake-pie/9852876e3955e896dc080efda8fa2620

        private bool? mmFound = null;
        private LoadingSystem mmLoadSys;
        private bool mmPostLoad = false;
        private static readonly Version mmPostLoadSupported = new Version(2, 6, 7);

        private bool MMAbsentOrReady()
        {
            if (!mmFound.HasValue) mmFound = findMM();
            return (mmFound == false) || (mmLoadSys?.IsReady() ?? true);
        }

        private bool findMM()
        {
            Log("Looking for Module Manager...");

            // Scan assemblies for any version, early return if none found
            if (AssemblyLoader.loadedAssemblies
                .Where(a => a.assembly.GetName().Name == "ModuleManager")
                .Count() == 0
            )
            {
                Log("... none found");
                return false;
            }

            // Rather than muck about with the list of all loaded assemblies
            // and trying to figure out which of multiple MM is the "correct" one
            // just let them sort it out among themselves and pick out the survivor
            LoadingSystem[] loaders = FindObjectsOfType<LoadingSystem>();

            // ModuleManager may have other LoadingSystems for save fixing, bug workarounds, etc, e.g. Fix16
            // so we *must* get the specific one, and not just any LoadingSystem in MM's namespace
            foreach (LoadingSystem loader in loaders)
            {
                Type t = loader.GetType();
                string name = t.FullName;
                if (name == "ModuleManager.PostPatchLoader" || name == "ModuleManager.MMPatchLoader")
                {
                    mmLoadSys = loader;
                    Version v = t.Assembly.GetName().Version;
                    mmPostLoad = (v >= mmPostLoadSupported);
                    Log($"... found {name} from MM ver {v.ToString()}. ModuleManagerPostLoad() will{(mmPostLoad?" ":" not ")}be used.");
                    return true;
                }
            }

            Log("... unsupported version found, it will be ignored");
            return false;
        }
        #endregion MM Compatibility

        #region Utility
        private static void Log(string msg)
        {
            Debug.Log("[unBlur] " + msg);
        }
        #endregion Utility
    }

    public static class UnBlurExtensions
    {
        public static bool isDXT(this TextureFormat tf){
            switch (tf)
            {
                case TextureFormat.DXT1:
                case TextureFormat.DXT1Crunched:
                case TextureFormat.DXT5:
                case TextureFormat.DXT5Crunched:
                    return true;
                default:
                    return false;
            }
        }
        public static bool isDXT1(this TextureFormat tf){
            switch (tf)
            {
                case TextureFormat.DXT1:
                case TextureFormat.DXT1Crunched:
                    return true;
                default:
                    return false;
            }
        }
        public static bool isDXT5(this TextureFormat tf){
            switch (tf)
            {
                case TextureFormat.DXT5:
                case TextureFormat.DXT5Crunched:
                    return true;
                default:
                    return false;
            }
        }
        public static bool canSetPixels32(this TextureFormat tf){
            switch (tf)
            {
                case TextureFormat.ARGB32:
                case TextureFormat.RGBA32:
                    return true;
                default:
                    return false;
            }
        }
        public static bool canSetPixels(this TextureFormat tf){
            switch (tf)
            {
                case TextureFormat.RGB24:
                case TextureFormat.Alpha8:
                case TextureFormat.ARGB32:
                case TextureFormat.RGBA32:
                    return true;
                default:
                    return false;
            }
        }
    }
}
