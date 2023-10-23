﻿using Newtonsoft.Json;
using QuestPatcher.Core.Models;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using AssetsTools.NET;
using AssetsTools.NET.Extra;
using QuestPatcher.Axml;
using QuestPatcher.Core.Modding;
using Serilog;
using QuestPatcher.Zip;
using System.Net.Http;
using System.Runtime.CompilerServices;

namespace QuestPatcher.Core.Patching
{
    /// <summary>
    /// Handles patching an app with the modloader.
    /// </summary>
    public class PatchingManager : INotifyPropertyChanged
    {
        private static readonly Uri AndroidNamespaceUri = new("http://schemas.android.com/apk/res/android");

        // Attribute resource IDs, used during manifest patching
        private const int NameAttributeResourceId = 16842755;
        private const int RequiredAttributeResourceId = 16843406;
        private const int DebuggableAttributeResourceId = 16842767;
        private const int LegacyStorageAttributeResourceId = 16844291;
        private const int ValueAttributeResourceId = 16842788;

        public PatchingStage PatchingStage { get => _patchingStage; private set { if (_patchingStage != value) { _patchingStage = value; NotifyPropertyChanged(); } } }
        private PatchingStage _patchingStage = PatchingStage.NotStarted;

        public event PropertyChangedEventHandler? PropertyChanged;

        private ApkInfo? InstalledApp => _installManager.InstalledApp;

        private readonly Config _config;
        private readonly AndroidDebugBridge _debugBridge;
        private readonly SpecialFolders _specialFolders;
        private readonly ExternalFilesDownloader _filesDownloader;
        private readonly IUserPrompter _prompter;
        private readonly ModManager _modManager;
        private readonly InstallManager _installManager;

        private readonly string _patchedApkPath;
        private Dictionary<string, Dictionary<string, string>>? _libUnityIndex;

        public PatchingManager(Config config, AndroidDebugBridge debugBridge, SpecialFolders specialFolders, ExternalFilesDownloader filesDownloader, IUserPrompter prompter, ModManager modManager, InstallManager installManager)
        {
            _config = config;
            _debugBridge = debugBridge;
            _specialFolders = specialFolders;
            _filesDownloader = filesDownloader;
            _prompter = prompter;
            _modManager = modManager;

            _patchedApkPath = Path.Combine(specialFolders.PatchingFolder, "patched.apk");
            _installManager = installManager;
        }

        private void NotifyPropertyChanged([CallerMemberName] string propertyName = "")
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        private async Task<TempFile?> GetUnstrippedUnityPath()
        {
            var client = new HttpClient();
            // Only download the index once
            if (_libUnityIndex == null)
            {
                Log.Debug("Downloading libunity index for the first time . . .");
                JsonSerializer serializer = new();

                try
                {
                    string data = await client.GetStringAsync("https://raw.githubusercontent.com/Lauriethefish/QuestUnstrippedUnity/main/index.json");

                    using StringReader stringReader = new(data);
                    using JsonReader reader = new JsonTextReader(stringReader);

                    _libUnityIndex = serializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(reader);
                }
                catch (HttpRequestException ex)
                {
                    Log.Warning(ex, "Failed to download libunity index");
                    return null;
                }

            }

            Log.Information("Checking index for unstripped libunity.so . . .");
            Debug.Assert(InstalledApp != null);
            Debug.Assert(_libUnityIndex != null);

            // The versions are separated per version of each app, since apps may change their unity version
            _libUnityIndex.TryGetValue(_config.AppId, out var availableVersions);

            if (availableVersions == null)
            {
                Log.Warning("Unstripped libunity not found for this app");
                return null;
            }

            availableVersions.TryGetValue(InstalledApp.Version, out string? correctVersion);

            if (correctVersion == null)
            {
                Log.Warning($"Unstripped libunity found for other versions of this app, but not {InstalledApp.Version}");
                return null;
            }

            Log.Information("Unstripped libunity found. Downloading . . .");

            TempFile tempDownloadPath = _specialFolders.GetTempFile();
            try
            {
                await _filesDownloader.DownloadUrl(
                    $"https://raw.githubusercontent.com/Lauriethefish/QuestUnstrippedUnity/main/versions/{correctVersion}.so",
                    tempDownloadPath.Path, "libunity.so");

                return tempDownloadPath;
            }
            catch
            {
                tempDownloadPath.Dispose();
                throw;
            }
        }

        /// <summary>
        /// Patches the manifest of the APK to add the permissions/features specified in <see cref="PatchingPermissions"/> in the <see cref="Config"/>.
        /// </summary>
        /// <param name="apk">The apk with the manifest to patch</param>
        /// <exception cref="PatchingException">If the given archive does not contain an <code>AndroidManifest.xml</code> file</exception>
        private void PatchManifestSync(ApkZip apk)
        {
            if (!apk.Entries.Contains(InstallManager.ManifestPath))
            {
                throw new PatchingException($"APK missing {InstallManager.ManifestPath} to patch");
            }

            // The AMXL loader requires a seekable stream
            using var ms = new MemoryStream();
            using (Stream stream = apk.OpenReader(InstallManager.ManifestPath))
            {
                stream.CopyTo(ms);
            }

            ms.Position = 0;
            Log.Information("Loading manifest as AXML . . .");
            AxmlElement manifest = AxmlLoader.LoadDocument(ms);

            // First we add permissions and features to the APK for modding
            List<string> addingPermissions = new();
            List<string> addingFeatures = new();
            PatchingPermissions permissions = _config.PatchingPermissions;
            if (permissions.ExternalFiles)
            {
                // Technically, we only need READ_EXTERNAL_STORAGE and WRITE_EXTERNAL_STORAGE, but we also add MANAGE_EXTERNAL_STORAGE as this is what Android 11 needs instead
                addingPermissions.AddRange(new[] {
                    "android.permission.READ_EXTERNAL_STORAGE",
                    "android.permission.WRITE_EXTERNAL_STORAGE",
                    "android.permission.MANAGE_EXTERNAL_STORAGE",
                });
            }

            if (permissions.Microphone)
            {
                Log.Information("Adding microphone permission request . . .");
                addingPermissions.Add("android.permission.RECORD_AUDIO");
            }

            if (permissions.HandTrackingType != HandTrackingVersion.None)
            {
                // For some reason these are separate permissions, but we need both of them
                addingPermissions.AddRange(new[]
                {
                    "oculus.permission.handtracking",
                    "com.oculus.permission.HAND_TRACKING"
                });
                // Tell Android (and thus Oculus home) that this app supports hand tracking and we can launch the app with it
                addingFeatures.Add("oculus.software.handtracking");
            }

            // Find which features and permissions already exist to avoid adding existing ones
            ISet<string> existingPermissions = GetExistingChildren(manifest, "uses-permission");
            ISet<string> existingFeatures = GetExistingChildren(manifest, "uses-feature");

            foreach (string permission in addingPermissions)
            {
                if (existingPermissions.Contains(permission)) { continue; } // Do not add existing permissions

                Log.Information($"Adding permission {permission}");
                AxmlElement permElement = new("uses-permission");
                AddNameAttribute(permElement, permission);
                manifest.Children.Add(permElement);
            }

            foreach (string feature in addingFeatures)
            {
                if (existingFeatures.Contains(feature)) { continue; } // Do not add existing features

                Log.Information($"Adding feature {feature}");
                AxmlElement featureElement = new("uses-feature");
                AddNameAttribute(featureElement, feature);

                // TODO: User may want the feature to be suggested instead of required
                featureElement.Attributes.Add(new AxmlAttribute("required", AndroidNamespaceUri, RequiredAttributeResourceId, false));
                manifest.Children.Add(featureElement);
            }

            // Now we need to add the legacyStorageSupport and debuggable flags
            AxmlElement appElement = manifest.Children.Single(element => element.Name == "application");
            if (permissions.Debuggable && !appElement.Attributes.Any(attribute => attribute.Name == "debuggable"))
            {
                Log.Information("Adding debuggable flag . . .");
                appElement.Attributes.Add(new AxmlAttribute("debuggable", AndroidNamespaceUri, DebuggableAttributeResourceId, true));
            }

            if (permissions.ExternalFiles && !appElement.Attributes.Any(attribute => attribute.Name == "requestLegacyExternalStorage"))
            {
                Log.Information("Adding legacy external storage flag . . .");
                appElement.Attributes.Add(new AxmlAttribute("requestLegacyExternalStorage", AndroidNamespaceUri, LegacyStorageAttributeResourceId, true));
            }


            switch (permissions.HandTrackingType)
            {
                case HandTrackingVersion.None:
                case HandTrackingVersion.V1:
                    Log.Debug("No need for any extra hand tracking metadata (v1/no tracking)");
                    break;
                case HandTrackingVersion.V1HighFrequency:
                    Log.Information("Adding high-frequency V1 hand-tracking. . .");
                    AxmlElement frequencyElement = new("meta-data");
                    AddNameAttribute(frequencyElement, "com.oculus.handtracking.frequency");
                    frequencyElement.Attributes.Add(new AxmlAttribute("value", AndroidNamespaceUri, ValueAttributeResourceId, "HIGH"));
                    appElement.Children.Add(frequencyElement);
                    break;
                case HandTrackingVersion.V2:
                    Log.Information("Adding V2 hand-tracking. . .");
                    frequencyElement = new("meta-data");
                    AddNameAttribute(frequencyElement, "com.oculus.handtracking.version");
                    frequencyElement.Attributes.Add(new AxmlAttribute("value", AndroidNamespaceUri, ValueAttributeResourceId, "V2.0"));
                    appElement.Children.Add(frequencyElement);
                    break;
            }

            // Save the manifest using the AXML library
            Log.Information("Saving manifest as AXML . . .");

            ms.SetLength(0);
            ms.Position = 0;
            AxmlSaver.SaveDocument(ms, manifest);
            ms.Position = 0;

            apk.AddFile(InstallManager.ManifestPath, ms, CompressionLevel.Optimal);
        }

        /// <summary>
        /// Scans the attributes of the children of the given element for their "name" attribute.
        /// </summary>
        /// <param name="manifest"></param>
        /// <param name="childNames"></param>
        /// <returns>A set of the values of the "name" attributes of children (does not error on children without this attribute)</returns>
        private ISet<string> GetExistingChildren(AxmlElement manifest, string childNames)
        {
            HashSet<string> result = new();

            foreach (AxmlElement element in manifest.Children)
            {
                if (element.Name != childNames) { continue; }

                List<AxmlAttribute> nameAttributes = element.Attributes.Where(attribute => attribute.Namespace == AndroidNamespaceUri && attribute.Name == "name").ToList();
                // Only add children with the name attribute
                if (nameAttributes.Count > 0) { result.Add((string) nameAttributes[0].Value); }
            }

            return result;
        }

        /// <summary>
        /// Adds a "name" attribute to the given element, with the given value.
        /// </summary>
        /// <param name="element">The element to add the attribute to</param>
        /// <param name="name">The value to put in the name attribute</param>
        private void AddNameAttribute(AxmlElement element, string name)
        {
            element.Attributes.Add(new AxmlAttribute("name", AndroidNamespaceUri, NameAttributeResourceId, name));
        }

        /// <summary>
        /// Modifies the given APK to support normal android devices, not just VR headsets.
        /// </summary>
        /// <param name="apk">APK to modify</param>
        /// <param name="ovrPlatformSdkPath">Path to the ovrPlatformSdk ZIP file to fetch a necessary library from</param>
        /// <exception cref="PatchingException">If the APK file was missing essential files, or otherwise not a valid unity game for adding flatscreen support.</exception>
        private void AddFlatscreenSupportSync(ApkZip apk, string ovrPlatformSdkPath)
        {
            const string bootCfgPath = "assets/bin/Data/boot.config";
            const string globalGameManagersPath = "assets/bin/Data/globalgamemanagers";
            const string ovrPlatformLoaderPath = "lib/arm64-v8a/libovrplatformloader.so";

            if (!apk.ContainsFile(bootCfgPath))
            {
                throw new PatchingException("boot.config must exist on a Beat Saber installation");
            }


            string bootCfgContents;
            using (var bootCfgStream = apk.OpenReader(bootCfgPath))
            using (var reader = new StreamReader(bootCfgStream))
            {
                bootCfgContents = (reader.ReadToEnd())
                    .Replace("vr-enabled=1", "vr-enabled=0")
                    .Replace("vr-device-list=Oculus", "vr-device-list=");
            }

            using (var newBootCfg = new MemoryStream())
            using (var newBootCfgWriter = new StreamWriter(newBootCfg))
            {
                newBootCfgWriter.Write(bootCfgContents);
                newBootCfgWriter.Flush();

                newBootCfg.Position = 0;
                apk.AddFile(bootCfgPath, newBootCfg, CompressionLevel.Optimal);

            }

            if (!apk.ContainsFile(globalGameManagersPath))
            {
                throw new PatchingException("globalgamemanagers must exist on a Beat Saber installation");
            }

            var am = new AssetsManager();

            using var replacementContents = new MemoryStream();
            using (var gameManagersStream = apk.OpenReader(globalGameManagersPath))
            {
                var inst = am.LoadAssetsFile(gameManagersStream, "globalgamemanagers", false);

                var inf = inst.table.GetAssetsOfType((int) AssetClassID.BuildSettings).Single();

                Assembly assembly = Assembly.GetExecutingAssembly();
                using Stream? classPkgStream = assembly.GetManifestResourceStream("QuestPatcher.Core.Resources.classdata.tpk");
                if (classPkgStream == null)
                {
                    throw new PatchingException("Could not find classdata.tpk in resources");
                }
                am.LoadClassPackage(classPkgStream);
                am.LoadClassDatabaseFromPackage(inst.file.typeTree.unityVersion);

                var type = am.GetTypeInstance(inst, inf);
                var baseField = type.GetBaseField();

                baseField.Get("enabledVRDevices").GetChildrenList()[0].SetChildrenList(Array.Empty<AssetTypeValueField>());
                var newBytes = baseField.WriteToByteArray();

                var replacer = new AssetsReplacerFromMemory(0, inf.index, (int) inf.curFileType, 0xffff, newBytes);

                using var writer = new AssetsFileWriter(replacementContents);
                inst.file.Write(writer, 0, new List<AssetsReplacer> { replacer });
                am.UnloadAllAssetsFiles();
            }

            replacementContents.Position = 0;
            apk.AddFile(globalGameManagersPath, replacementContents, CompressionLevel.Optimal);

            using var sdkArchive = ZipFile.OpenRead(ovrPlatformSdkPath);
            var downgradedLoaderEntry = sdkArchive.GetEntry("Android/libs/arm64-v8a/libovrplatformloader.so")
                                        ?? throw new PatchingException("No libovrplatformloader.so found in downloaded OvrPlatformSdk");
            using var downloadedLoaderStream = downgradedLoaderEntry.Open();

            apk.AddFile(ovrPlatformLoaderPath, downloadedLoaderStream, CompressionLevel.Optimal);
        }

        /// <summary>
        /// Makes the modifications to the APK to support mods.
        /// </summary>
        /// <param name="mainPath">Path of the libmain file to replace</param>
        /// <param name="modloaderPath">Path of the libmodloader to replace</param>
        /// <param name="unityPath">Optionally, a path to a replacement libunity.so</param>
        /// <param name="libsDirectory">The directory where the SO files are stored in the APK</param>
        /// <param name="ovrPlatformSdkPath">Path to the OVR platform SDK ZIP, used for patching with flatscreen support. Must be non-null if flatscreen support is enabled.</param>
        /// <param name="apk">The APK to patch</param>
        private void ModifyApkSync(string mainPath, string modloaderPath, string? unityPath, string? ovrPlatformSdkPath, string libsDirectory, ApkZip apk)
        {
            Log.Information("Copying libmain.so and libmodloader.so . . .");
            AddFileToApkSync(mainPath, Path.Combine(libsDirectory, "libmain.so"), false, apk);
            AddFileToApkSync(modloaderPath, Path.Combine(libsDirectory, "libmodloader.so"), true, apk);

            if (unityPath != null)
            {
                Log.Information("Adding unstripped libunity.so . . .");
                AddFileToApkSync(unityPath, Path.Combine(libsDirectory, "libunity.so"), false, apk);
            }

            if (_config.PatchingPermissions.FlatScreenSupport)
            {
                Log.Information("Adding flatscreen support . . .");
                AddFlatscreenSupportSync(apk, ovrPlatformSdkPath!);
            }

            Log.Information("Patching manifest . . .");
            PatchManifestSync(apk);

            Log.Information("Adding tag");
            var emptyStream = new MemoryStream();
            apk.AddFile("modded", emptyStream, null); // TODO: Use new tag
        }

        /// <summary>
        /// Copies the file with the given path into the APK.
        /// </summary>
        /// <param name="filePath">The path to the file to copy into the APK</param>
        /// <param name="apkFilePath">The name of the file in the APK to create</param>
        /// <param name="failIfExists">Whether to throw an exception if the file already exists</param>
        /// <param name="apk">The apk to copy the file into</param>
        /// <exception cref="PatchingException">If the file already exists in the APK, if configured to throw.</exception>
        private void AddFileToApkSync(string filePath, string apkFilePath, bool failIfExists, ApkZip apk)
        {
            if (failIfExists && apk.ContainsFile(apkFilePath))
            {
                throw new PatchingException($"File {apkFilePath} already existed in the APK. Is the app already patched?");
            }

            using var fileStream = File.OpenRead(filePath);
            apk.AddFile(apkFilePath, fileStream, CompressionLevel.Optimal);
        }

        /// <summary>
        /// Begins patching the currently installed APK, then uninstalls it and installs the modded copy. (must be pulled before calling this)
        /// <exception cref="FileDownloadFailedException">If downloading files necessary to mod the APK fails</exception>
        /// </summary>
        public async Task PatchApp()
        {
            if (InstalledApp == null)
            {
                throw new NullReferenceException("Cannot patch before installed app has been checked");
            }

            if (!InstalledApp.Is64Bit)
            {
                Log.Warning("App is 32 bit!");
                if (!await _prompter.Prompt32Bit()) // Prompt the user to ask if they would like to continue, even though BS-hook doesn't work on 32 bit apps
                {
                    return;
                }
            }

            Log.Information("Downloading files . . .");
            PatchingStage = PatchingStage.FetchingFiles;

            // First make sure that we have all necessary files downloaded, including the libmain and libmodloader
            string libsPath = InstalledApp.Is64Bit ? "lib/arm64-v8a" : "lib/armeabi-v7a";
            string mainPath;
            string modloaderPath;
            string? ovrPlatformSdkPath = null;
            if (InstalledApp.Is64Bit)
            {
                mainPath = await _filesDownloader.GetFileLocation(ExternalFileType.Main64);
                modloaderPath = await _filesDownloader.GetFileLocation(ExternalFileType.Modloader64);
            }
            else
            {
                mainPath = await _filesDownloader.GetFileLocation(ExternalFileType.Main32);
                modloaderPath = await _filesDownloader.GetFileLocation(ExternalFileType.Modloader32);
            }
            if (_config.PatchingPermissions.FlatScreenSupport)
            {
                ovrPlatformSdkPath = await _filesDownloader.GetFileLocation(ExternalFileType.OvrPlatformSdk);
            }

            using var libUnityFile = await GetUnstrippedUnityPath();
            if (libUnityFile == null)
            {
                if (!await _prompter.PromptUnstrippedUnityUnavailable())
                {
                    return;
                }
            }

            PatchingStage = PatchingStage.MovingToTemp;
            Log.Information("Copying APK to temporary location . . .");
            if (File.Exists(_patchedApkPath))
            {
                File.Delete(_patchedApkPath);
            }

            // No asynchronous File.Copy unfortunately
            await Task.Run(() => File.Copy(InstalledApp.Path, _patchedApkPath));

            // Then actually do the patching, using the APK reader, which is synchronous
            PatchingStage = PatchingStage.Patching;
            Log.Information("Copying files to patch the apk . . .");
            using var apkStream = File.Open(_patchedApkPath, FileMode.Open);
            ApkZip apk = await Task.Run(() => ApkZip.Open(apkStream));
            try
            {
                await Task.Run(() => ModifyApkSync(mainPath, modloaderPath, libUnityFile?.Path, ovrPlatformSdkPath, libsPath, apk));

                Log.Information("Signing APK . . .");
                PatchingStage = PatchingStage.Signing;
            }
            finally
            {
                await Task.Run(() => { apk.Dispose(); });
            }

            Log.Information("Uninstalling the default APK . . .");
            Log.Information("Backing up data directory");
            string? backupPath;
            try
            {
                backupPath = await _installManager.CreateDataBackup();
            }
            catch (Exception ex)
            {
                Log.Error($"Failed to create data backup: {ex}");
                backupPath = null; // Indicate that the backup failed
            }

            PatchingStage = PatchingStage.UninstallingOriginal;
            await _debugBridge.UninstallApp(_config.AppId);

            Log.Information("Installing the modded APK . . .");
            PatchingStage = PatchingStage.InstallingModded;
            await _debugBridge.InstallApp(_patchedApkPath);

            if (backupPath != null)
            {
                Log.Information("Restoring data backup");
                try
                {
                    await _installManager.RestoreDataBackup(backupPath);
                }
                catch (Exception ex)
                {
                    Log.Error($"Failed to restore data backup: {ex}");
                }
            }


            // Recreate the mod directories as they will not be present after the uninstall/backup restore
            await _modManager.CreateModDirectories();

            await _installManager.NewApkInstalled(_patchedApkPath);

            Log.Information("Patching complete!");

        }
    }
}
