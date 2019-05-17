using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using UnityEngine;
using DDSHeaders;
using KSP.UI.Screens.DebugToolbar;

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

            GameEvents.onLevelWasLoaded.Add(EnableConsoleCommand);
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
                do {
                    yield return null;
                } while (!MMAbsentOrReady());
            }

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
            Log("Processing...\n#### BEGIN BATCH PROCESSING ####\n{");

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

            Debug.Log("}\n#### END BATCH PROCESSING ####");
            if (verbose) DumpTextureInfo();
            progressTitle = "unBlur: Done!";
            ready = true;
        }
        #endregion Batch Processing

        #region Core
        private UrlDir GameDataDir;
        private bool debug = false;
        private bool verbose = false;

        public Texture2D GetTexture(string url, bool asNormalMap = false, bool compress = true)
        {
            GameDatabase.TextureInfo texInfo = GameDatabase.Instance.GetTextureInfo(url);
            if (texInfo == null)
            {
                Log($"Unable to find texture {url} in GameDatabase");
                return null;
            }
            DisableMipmaps(texInfo, compress);
            if (asNormalMap)
                return texInfo.normalMap;
            else
                return texInfo.texture;
        }

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
                if (compress && !texInfo.isCompressed) // don't compress again if stock KSP already did it (or tried and failed)
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
            if (compress || texInfo.isCompressed || oldTex.format.isDXT())
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

        private void DumpTextureInfo(string url)
        {
            GameDatabase.TextureInfo texInfo = GameDatabase.Instance.GetTextureInfo(url);
            if (texInfo == null)
                Log($"Unable to dump GameDatabase.TextureInfo for {url} -- texture not found");
            else
                Log(FormatTextureInfo(texInfo));
        }

        private string FormatTextureInfo(GameDatabase.TextureInfo texInfo)
        {
            return
                (texInfo.isNormalMap  ? "N" : "-") +
                (texInfo.isReadable   ? "R" : "-") +
                (texInfo.isCompressed ? "C" : "-") +
                (texInfo.texture.format.isDXT() ? "C" : "-") +
                String.Format(" {0,4:D}x{1,-4:D} {2,-2:D} ", texInfo.texture.width, texInfo.texture.height, texInfo.texture.mipmapCount) +
                texInfo.texture.format.ToString("G").PadRight(16) +
                texInfo.name;
        }
        #endregion Core

        #region Debug Console
        private const string DbgCommand = "unBlur";
        private const string DbgHelpString = "unBlur console tool.";

        private const string DbgCmdHelp = "help";
        private const string DbgCmdInfo = "info";
        private const string DbgCmdTxtr = "texture";
        private const string DbgCmdFldr = "folder";
        private const string DbgCmdDump = "dumpDB";

        private static readonly Regex DbgParser = new Regex(@"^\s*(?<command>\S+)(?:\s+(?<target>.+))?\s*$");

        private static readonly string DbgMsgUsage = $"Type \"/{DbgCommand} {DbgCmdHelp}\" for usage.";
        private static readonly string DbgMsgUnparseable = $"Unable to parse command. {DbgMsgUsage}";
        private static readonly string DbgMsgInvalidCmd = $"{{0}} is not a valid command. {DbgMsgUsage}";
        private static readonly string DbgMsgHelpText = $@"{DbgHelpString}
Usage:

/{DbgCommand} {DbgCmdHelp}
Displays this help message.

/{DbgCommand} {DbgCmdInfo} <target>
Dump GameDatabase.TextureInfo data for the target texture
e.g.:
    /{DbgCommand} {DbgCmdInfo} modA/icon/toolbar_on

/{DbgCommand} {DbgCmdTxtr} <target>
Disable mipmaps on a single target texture
e.g.:
    /{DbgCommand} {DbgCmdTxtr} modA/icon/toolbar_off

/{DbgCommand} {DbgCmdFldr} <target>
Disable mipmaps on all textures in a single folder
e.g.:
    /{DbgCommand} {DbgCmdFldr} modA/icon/

/{DbgCommand} {DbgCmdDump}
Dump GameDatabase.TextureInfo data for all textures in the GameDatabase
May be truncated in the console display, if so, flush the log file to disk and view it there instead";

        private void EnableConsoleCommand(GameScenes gs)
        {
            if (gs != GameScenes.MAINMENU) return;
            DebugScreenConsole.AddConsoleCommand(DbgCommand, DbgOnCommand, DbgHelpString);
            GameEvents.onLevelWasLoaded.Remove(EnableConsoleCommand);
        }

        private void DbgOnCommand(string argStr)
        {
            if (String.IsNullOrEmpty(argStr))
                argStr = DbgCmdHelp;
            Match m = DbgParser.Match(argStr);
            if (!m.Success)
            {
                Log(DbgMsgUnparseable);
                return;
            }

            string command = m.Groups["command"].Value;
            switch (command)
            {
                case DbgCmdHelp:
                    Log(DbgMsgHelpText);
                    break;
                case DbgCmdInfo:
                    DumpTextureInfo(m.Groups["target"].Value);
                    break;
                case DbgCmdTxtr:
                    DisableMipmaps(m.Groups["target"].Value, true);
                    break;
                case DbgCmdFldr:
                    DisableMipmapsInFolder(m.Groups["target"].Value, true);
                    break;
                case DbgCmdDump:
                    DumpTextureInfo();
                    break;
                default:
                    Log(String.Format(DbgMsgInvalidCmd, command));
                    break;
            }
        }
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
        // https://docs.microsoft.com/en-us/windows/desktop/direct3ddds/dx-graphics-dds-pguide
        // https://kerbalspaceprogram.com/api/namespace_d_d_s_headers.html
        private static Texture2D LoadDDS(UrlDir.UrlFile file, bool mipmaps)
        {
            string filepath = file.fullPath;
            Log("LoadDDS: Loading DDS file from "+filepath);
            if (!File.Exists(filepath))
            {
                Log("LoadDDS: File not found!");
                return null;
            }
            using (BinaryReader reader = new BinaryReader(File.Open(filepath, FileMode.Open, FileAccess.Read)))
            {
                if (reader.ReadUInt32() != DDSValues.uintMagic)
                {
                    Log("LoadDDS: Not a DDS file!");
                    return null;
                }
                DDSHeader header = new DDSHeader(reader);
                if (header.ddspf.dwFourCC == DDSValues.uintDX10)
                {
                    DDSHeaderDX10 headerDX10 = new DDSHeaderDX10(reader);
                }

                mipmaps = mipmaps && ((header.dwCaps & DDSPixelFormatCaps.TEXTURE) != 0);
                // uint mipmapcount = (header.dwFlags & 0x20000 == 0) ? 1 : header.dwMipMapCount;

                string dwFourCC;
                if (header.ddspf.dwFourCC == DDSValues.uintDXT1)
                {
                    try
                    {
                        Texture2D result = new Texture2D((int) header.dwWidth, (int) header.dwHeight, TextureFormat.DXT1, mipmaps);
                        result.LoadRawTextureData(reader.ReadBytes(
                            mipmaps ? ((int) (reader.BaseStream.Length - reader.BaseStream.Position)) :
                            Math.Max(1, ( ((int) header.dwWidth + 3) / 4 ) ) * Math.Max(1, ( ((int) header.dwHeight + 3) / 4 ) ) * 8
                        ));
                        result.Apply(false, true);
                        return result;
                    }
                    catch (UnityException e)
                    {
                        Log("LoadDDS: error loading DXT1: " + e.Message);
                        return null;
                    }
                }
                else if (header.ddspf.dwFourCC == DDSValues.uintDXT3)
                {
                    try
                    {
                        // Undocumented DXT3: see notes at UnBlurExtensions.isDXT()
                        Texture2D result = new Texture2D((int) header.dwWidth, (int) header.dwHeight, (TextureFormat) 11, mipmaps);
                        result.LoadRawTextureData(reader.ReadBytes(
                            mipmaps ? ((int) (reader.BaseStream.Length - reader.BaseStream.Position)) :
                            Math.Max(1, ( ((int) header.dwWidth + 3) / 4 ) ) * Math.Max(1, ( ((int) header.dwHeight + 3) / 4 ) ) * 16
                        ));
                        result.Apply(false, true);
                        return result;
                    }
                    catch (UnityException e)
                    {
                        Log("LoadDDS: error loading DXT3: " + e.Message);
                        return null;
                    }
                }
                else if (header.ddspf.dwFourCC == DDSValues.uintDXT5)
                {
                    try
                    {
                        Texture2D result = new Texture2D((int) header.dwWidth, (int) header.dwHeight, TextureFormat.DXT5, mipmaps);
                        result.LoadRawTextureData(reader.ReadBytes(
                            mipmaps ? ((int) (reader.BaseStream.Length - reader.BaseStream.Position)) :
                            Math.Max(1, ( ((int) header.dwWidth + 3) / 4 ) ) * Math.Max(1, ( ((int) header.dwHeight + 3) / 4 ) ) * 16
                        ));
                        result.Apply(false, true);
                        return result;
                    }
                    catch (UnityException e)
                    {
                        Log("LoadDDS: error loading DXT5: " + e.Message);
                        return null;
                    }
                }
                else if (header.ddspf.dwFourCC == DDSValues.uintDXT2) dwFourCC = "DXT2";
                else if (header.ddspf.dwFourCC == DDSValues.uintDXT4) dwFourCC = "DXT4";
                else if (header.ddspf.dwFourCC == DDSValues.uintDX10) dwFourCC = "DX10";
                else
                {
                    Log("LoadDDS: Unrecognized format: " + header.ddspf.dwFourCC.ToString("X"));
                    return null;
                }
                Log($"LoadDDS: Format {dwFourCC} is not supported.");
                return null;
            }
        }

        private static void Log(string msg)
        {
            Debug.Log("[unBlur] " + msg);
        }
        #endregion Utility
    }

    public static class UnBlurExtensions
    {
        // DXT3 is no longer documented or supported, but still there
        // https://forum.unity.com/threads/how-to-ensure-certain-compression-formats-per-texture.510951/
        // DXT1 = 10, DXT5 = 12 ... infer DXT3 = 11 which is indeed observed in a few cases from GameDatabase TextureInfo dumps
        // but DXT3Crunched does not appear to have been a thing (DXT1Crunched = 28, DXT5Crunched = 29)
        // https://github.com/Unity-Technologies/UnityCsReference/blob/2017.3/Runtime/Export/GraphicsEnums.cs#L263-L264
        public static bool isDXT(this TextureFormat tf){
            switch (tf)
            {
                case TextureFormat.DXT1:
                case TextureFormat.DXT1Crunched:
                case (TextureFormat) 11:
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
        public static bool isDXT3(this TextureFormat tf){
            if (tf == (TextureFormat) 11) return true;
            return false;
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
