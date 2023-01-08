﻿using Natsurainko.FluentCore.Interface;
using Natsurainko.FluentCore.Model.Download;
using Natsurainko.FluentCore.Model.Install;
using Natsurainko.FluentCore.Model.Install.Forge;
using Natsurainko.FluentCore.Model.Parser;
using Natsurainko.FluentCore.Module.Parser;
using Natsurainko.FluentCore.Service;
using Natsurainko.Toolkits.IO;
using Natsurainko.Toolkits.Network;
using Natsurainko.Toolkits.Network.Downloader;
using Natsurainko.Toolkits.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;

namespace Natsurainko.FluentCore.Module.Installer;

public class MinecraftForgeInstaller : InstallerBase
{
    public string JavaPath { get; private set; }

    public ForgeInstallBuild ForgeBuild { get; private set; }

    public string PackageFile { get; private set; }

    public MinecraftForgeInstaller(
        IGameCoreLocator<IGameCore> coreLocator,
        ForgeInstallBuild build,
        string javaPath,
        string packageFile = null,
        string customId = null) : base(coreLocator, customId)
    {
        ForgeBuild = build;
        JavaPath = javaPath;
        PackageFile = packageFile;
    }

    public override async Task<InstallerResponse> InstallAsync()
    {
        try
        {
            #region Download Package

            OnProgressChanged("Downloading Forge Insatller Package", 0.0f);

            if (string.IsNullOrEmpty(PackageFile) || !File.Exists(PackageFile))
            {
                var downloadUrl = $"{(DownloadApiManager.Current.Host.Equals(DownloadApiManager.Mojang.Host) ? DownloadApiManager.Bmcl.Host : DownloadApiManager.Current.Host)}" +
                    $"/forge/download/{ForgeBuild.Build}";
                using var packageDownloader = new SimpleDownloader(new DownloadRequest()
                {
                    Url = downloadUrl,
                    Directory = GameCoreLocator.Root
                });

                packageDownloader.DownloadProgressChanged += (object sender, SimpleDownloaderProgressChangedEventArgs e) =>
                    OnProgressChanged("Downloading Forge Insatller Package", (float)(0.1f * e.Progress));

                var downloadResponse = await packageDownloader.CompleteAsync();

                if (downloadResponse.HttpStatusCode != System.Net.HttpStatusCode.OK)
                    throw new HttpRequestException(downloadResponse.HttpStatusCode.ToString());

                PackageFile = downloadResponse.Result.FullName;
            }

            #endregion

            #region Parse Package

            OnProgressChanged("Parsing Installer Package", 0.15f);

            using var archive = ZipFile.OpenRead(PackageFile);

            var installProfile = JObject.Parse(archive.GetEntry("install_profile.json").GetString());
            var entity = MinecraftForgeInstaller.GetVersionJsonEntity(archive, installProfile);

            var entityLibraries = new LibraryParser(entity.Libraries, GameCoreLocator.Root).GetLibraries();
            var installerLibraries = installProfile.ContainsKey("libraries")
                ? new LibraryParser(installProfile["libraries"].ToObject<IEnumerable<LibraryJsonEntity>>().ToList(), GameCoreLocator.Root).GetLibraries()
                : Array.Empty<LibraryResource>().AsEnumerable();

            var dataDictionary = installProfile.ContainsKey("data")
                ? installProfile["data"].ToObject<Dictionary<string, JObject>>()
                : new Dictionary<string, JObject>();

            var downloadLibraries = entityLibraries.Union(installerLibraries);

            if (!string.IsNullOrEmpty(CustomId))
                entity.Id = CustomId;

            #endregion

            #region Download Libraries

            OnProgressChanged("Downloading Libraries", 0.15f);

            using var downloader = new ParallelDownloader<LibraryResource>(downloadLibraries, x => x.ToDownloadRequest());
            downloader.DownloadProgressChanged += (object sender, ParallelDownloaderProgressChangedEventArgs e) =>
                OnProgressChanged($"Downloading Libraries {e.CompletedTasks}/{e.TotleTasks}", (float)(0.15f + 0.45f * e.Progress));

            if (DownloadApiManager.Current.Host.Equals(DownloadApiManager.Mojang.Host))
            {
                DownloadApiManager.Current = DownloadApiManager.Bmcl;
                downloader.DownloadCompleted += delegate { DownloadApiManager.Current = DownloadApiManager.Mojang; };
            }

            downloader.BeginDownload();
            var downloaderResponse = await downloader.CompleteAsync();

            #endregion

            #region Write Files

            OnProgressChanged("Writing Files", 0.70f);

            string forgeFolderId = $"{ForgeBuild.McVersion}-{ForgeBuild.ForgeVersion}";
            string forgeLibrariesFolder = Path.Combine(GameCoreLocator.Root.FullName, "libraries", "net", "minecraftforge", "forge", forgeFolderId);

            if (installProfile.ContainsKey("install"))
            {
                var lib = new LibraryResource
                {
                    Root = GameCoreLocator.Root,
                    Name = installProfile["install"]["path"].ToString()
                };

                archive.GetEntry(installProfile["install"]["filePath"].ToString()).ExtractTo(lib.ToFileInfo().FullName);
            }

            if (archive.GetEntry("maven/") != null)
            {
                archive.GetEntry($"maven/net/minecraftforge/forge/{forgeFolderId}/forge-{forgeFolderId}.jar")?
                    .ExtractTo(Path.Combine(forgeLibrariesFolder, $"forge-{forgeFolderId}.jar"));
                archive.GetEntry($"maven/net/minecraftforge/forge/{forgeFolderId}/forge-{forgeFolderId}-universal.jar")?
                    .ExtractTo(Path.Combine(forgeLibrariesFolder, $"forge-{forgeFolderId}-universal.jar"));
            }

            if (dataDictionary.Any())
            {
                archive.GetEntry("data/client.lzma").ExtractTo(Path.Combine(forgeLibrariesFolder, $"forge-{forgeFolderId}-clientdata.lzma"));
                archive.GetEntry("data/server.lzma").ExtractTo(Path.Combine(forgeLibrariesFolder, $"forge-{forgeFolderId}-serverdata.lzma"));
            }

            var versionJsonFile = new FileInfo(Path.Combine(GameCoreLocator.Root.FullName, "versions", entity.Id, $"{entity.Id}.json"));

            if (!versionJsonFile.Directory.Exists)
                versionJsonFile.Directory.Create();

            File.WriteAllText(versionJsonFile.FullName, entity.ToJson());

            #endregion

            #region Check Inherited Core

            await CheckInheritedCore(0.7f, 0.85f, ForgeBuild.McVersion);

            #endregion

            #region LegacyForgeInstaller Exit

            if (installProfile.ContainsKey("versionInfo"))
            {
                OnProgressChanged("Install Finished", 1.0f);

                return new InstallerResponse
                {
                    Success = true,
                    Exception = null,
                    GameCore = GameCoreLocator.GetGameCore(entity.Id)
                };
            }

            #endregion

            #region Parser Processor

            OnProgressChanged("Parsering Installer Processor", 0.85f);

            dataDictionary["BINPATCH"]["client"] = $"[net.minecraftforge:forge:{forgeFolderId}:clientdata@lzma]";
            dataDictionary["BINPATCH"]["server"] = $"[net.minecraftforge:forge:{forgeFolderId}:serverdata@lzma]";

            var replaceValues = new Dictionary<string, string>
            {
                { "{SIDE}", "client" },
                { "{MINECRAFT_JAR}", Path.Combine(GameCoreLocator.Root.FullName, "versions", ForgeBuild.McVersion, $"{ForgeBuild.McVersion}.jar") },
                { "{MINECRAFT_VERSION}", ForgeBuild.McVersion },
                { "{ROOT}", GameCoreLocator.Root.FullName },
                { "{INSTALLER}", PackageFile },
                { "{LIBRARY_DIR}", Path.Combine(GameCoreLocator.Root.FullName, "libraries") }
            };

            var replaceProcessorArgs = dataDictionary.ToDictionary(x => $"{{{x.Key}}}", x =>
            {
                string value = x.Value["client"].ToString();

                if (value.StartsWith("[") && value.EndsWith("]"))
                    return CombineLibraryName(value);

                return value;
            });

            var processors = installProfile["processors"].ToObject<IEnumerable<ForgeInstallProcessorModel>>()
                .Where(x =>
                {
                    if (!x.Sides.Any())
                        return true;

                    return x.Sides.Contains("client");
                }).Select(x =>
                {
                    x.Args = x.Args.Select(y =>
                    {
                        if (y.StartsWith("[") && y.EndsWith("]"))
                            return CombineLibraryName(y);

                        return y.Replace(replaceProcessorArgs).Replace(replaceValues);
                    }).ToList();

                    x.Outputs = x.Outputs.Select(kvp => (kvp.Key.Replace(replaceProcessorArgs), kvp.Value.Replace(replaceProcessorArgs))).ToDictionary(z => z.Item1, z => z.Item2);

                    return x;
                }).ToList();

            #endregion

            #region Run Processor

            var processes = new Dictionary<List<string>, List<string>>();

            foreach (var forgeInstallProcessor in processors)
            {
                var fileName = CombineLibraryName(forgeInstallProcessor.Jar);
                using var fileArchive = ZipFile.OpenRead(fileName);

                string mainClass = fileArchive.GetEntry("META-INF/MANIFEST.MF").GetString().Split("\r\n".ToCharArray()).First(x => x.Contains("Main-Class: ")).Replace("Main-Class: ", string.Empty);
                string classPath = string.Join(Path.PathSeparator.ToString(), new List<string> { forgeInstallProcessor.Jar }
                    .Concat(forgeInstallProcessor.Classpath)
                    .Select(x => new LibraryResource { Name = x, Root = GameCoreLocator.Root })
                    .Select(x => x.ToFileInfo().FullName));

                var args = new List<string>
                {
                    "-cp",
                    $"\"{classPath}\"",
                    mainClass
                };

                args.AddRange(forgeInstallProcessor.Args);

                using var process = Process.Start(new ProcessStartInfo(JavaPath)
                {
                    Arguments = string.Join(' '.ToString(), args),
                    UseShellExecute = false,
                    WorkingDirectory = GameCoreLocator.Root.FullName,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true
                });

                var outputs = new List<string>();
                var errorOutputs = new List<string>();

                process.OutputDataReceived += (_, args) =>
                {
                    if (!string.IsNullOrEmpty(args.Data))
                        outputs.Add(args.Data);
                };
                process.ErrorDataReceived += (_, args) =>
                {
                    if (!string.IsNullOrEmpty(args.Data))
                    {
                        outputs.Add(args.Data);
                        errorOutputs.Add(args.Data);
                    }
                };

                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                process.WaitForExit();
                processes.Add(outputs, errorOutputs);

                OnProgressChanged($"Running Installer Processor {processors.IndexOf(forgeInstallProcessor)}/{processors.Count}", 0.85f + 0.15f * (processors.IndexOf(forgeInstallProcessor) / (float)processors.Count));
            }

            #endregion

            #region Clean

            File.Delete(PackageFile);

            #endregion

            OnProgressChanged("Install Finished", 1.0f);

            return new()
            {
                Success = true,
                Exception = null,
                GameCore = GameCoreLocator.GetGameCore(entity.Id),
                DownloaderResponse = downloaderResponse
            };
        }
        catch (Exception ex)
        {
            return new()
            {
                Success = false,
                GameCore = null,
                Exception = ex
            };
        }
    }

    private static VersionJsonEntity GetVersionJsonEntity(ZipArchive archive, JObject installProfile)
    {
        if (installProfile.ContainsKey("versionInfo"))
            return installProfile["versionInfo"].ToObject<VersionJsonEntity>();

        var entry = archive.GetEntry("version.json");

        if (entry != null)
            return JsonConvert.DeserializeObject<VersionJsonEntity>(entry.GetString());

        return null;
    }

    private string CombineLibraryName(string name)
    {
        string libraries = Path.Combine(GameCoreLocator.Root.FullName, "libraries");

        foreach (var subPath in LibraryResource.FormatName(name.TrimStart('[').TrimEnd(']')))
            libraries = Path.Combine(libraries, subPath);

        return libraries;
    }

    public static async Task<string[]> GetSupportedMcVersionsAsync()
    {
        try
        {
            using var responseMessage = await HttpWrapper.HttpGetAsync($"{(DownloadApiManager.Current.Host.Equals(DownloadApiManager.Mojang.Host) ? DownloadApiManager.Bmcl.Host : DownloadApiManager.Current.Host)}/forge/minecraft");
            responseMessage.EnsureSuccessStatusCode();

            return JsonConvert.DeserializeObject<string[]>(await responseMessage.Content.ReadAsStringAsync());
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    public static async Task<ForgeInstallBuild[]> GetForgeBuildsFromMcVersionAsync(string mcVersion)
    {
        try
        {
            using var responseMessage = await HttpWrapper.HttpGetAsync($"{(DownloadApiManager.Current.Host.Equals(DownloadApiManager.Mojang.Host) ? DownloadApiManager.Bmcl.Host : DownloadApiManager.Current.Host)}/forge/minecraft/{mcVersion}");
            responseMessage.EnsureSuccessStatusCode();

            var list = JsonConvert.DeserializeObject<List<ForgeInstallBuild>>(await responseMessage.Content.ReadAsStringAsync());

            list.Sort((a, b) => a.Build.CompareTo(b.Build));
            list.Reverse();

            return list.ToArray();
        }
        catch
        {
            return Array.Empty<ForgeInstallBuild>();
        }
    }

    public static async Task<DownloadResponse> DownloadForgePackageFromBuildAsync(int build, DirectoryInfo directory)
    {
        var downloadUrl = $"{(DownloadApiManager.Current.Host.Equals(DownloadApiManager.Mojang.Host) ? DownloadApiManager.Bmcl.Host : DownloadApiManager.Current.Host)}" +
            $"/forge/download/{build}";

        return await SimpleDownloader.StartNewDownloadAsync(new DownloadRequest()
        {
            Url = downloadUrl,
            Directory = directory
        });
    }
}
