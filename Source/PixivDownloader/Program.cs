using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using Meowtrix.PixivApi;
using Meowtrix.PixivApi.Models;

namespace PixivDownloader
{
    class Program
    {
        private static readonly PixivClient _client = new();
        private static PixivApiClient _apiClient = new(true);
        private static IWebProxy _webProxy;
        private static string _directory;
        private static int _lastPid;
        private static int _downloadThread = 8;
        private static SemaphoreSlim _downloadSemaphore = new(_downloadThread);


        static async Task Main(string[] args)
        {
            // 设置当前工作目录
            Directory.SetCurrentDirectory(AppDomain.CurrentDomain.BaseDirectory);
            Console.WriteLine($"Current directory: {Environment.CurrentDirectory}");
            if (args.Length > 0 && args[0].StartsWith("pixiv:"))
            {
                Console.WriteLine("Writing auth url...");
                WriteTempFile(args[0]);
                return;
            }
            SetProxy();
            if (_webProxy != null)
            {
                _client.SetProxy(_webProxy);
                _apiClient = new PixivApiClient(_webProxy);
            }

            try
            {
                await Auth();

                SetTargetDirectory();

                while (true)
                {
                    WriteOneColorLine($"Running time: {DateTimeOffset.Now}", ConsoleColor.Green);
                    await SimpleDownloadFollowingIllusts();
                    WriteOneColorLine($"Waiting for next run: {DateTimeOffset.Now.AddHours(1)}", ConsoleColor.Green);
                    await Task.Delay(TimeSpan.FromHours(1));
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex);
            }

            Console.WriteLine("End.");
            Console.ReadKey();
        }

        private const string LastPidFile = "pd_last_pid";

        private static async Task SimpleDownloadFollowingIllusts()
        {
            if (File.Exists(LastPidFile) && int.TryParse(File.ReadAllText(LastPidFile), out _lastPid))
            {
                Console.WriteLine($"Start downloading from pid: {_lastPid}.");
            }
            else
            {
                Console.WriteLine("Are you sure download all following illusts or from a specified pid?");
                var pid = Console.ReadLine().Trim();
                if (string.IsNullOrEmpty(pid) || !int.TryParse(pid, out _lastPid))
                {
                    Console.WriteLine($"Start downloading all following illusts.");
                }
                else
                {
                    File.WriteAllText(LastPidFile, pid);
                    Console.WriteLine($"Start downloading from pid: {_lastPid}.");
                }
            }
            Console.WriteLine($"Downloading will start in 3 seconds...");
            await Task.Delay(TimeSpan.FromSeconds(3));
            ClearConsoleLine(Console.CursorTop - 1);
            Console.WriteLine("Get downloading illusts...");
            int currentPid = _lastPid;
            var downloadList = new List<Illust>();
            var illusts = _client.GetMyFollowingIllustsAsync();
            await foreach (var item in illusts)
            {
                if (item.Id <= _lastPid)
                {
                    break;
                }
                else if (item.Id > currentPid)
                {
                    currentPid = item.Id;
                }
                downloadList.Add(item);
            }

            ClearConsoleLine(Console.CursorTop - 1);
            if (downloadList.Count == 0)
            {
                Console.WriteLine("No new illust found.");
                return;
            }
            Console.WriteLine($"Downloading {downloadList.Count} illusts...");

            var failed = await DownloadTask(downloadList);
            while (failed.Count > 0)
            {
                Console.WriteLine($"Retry failed {failed.Count} illusts...");
                failed = await DownloadTask(failed);
            }

            File.WriteAllText(LastPidFile, currentPid.ToString());
            Console.WriteLine("Download Completed.");


            async Task<List<Illust>> DownloadTask(IEnumerable<Illust> illust)
            {
                var failedList = new List<Illust>();
                int cursorTop = Console.CursorTop;
                int completedCount = 0;
                await foreach (var item in BatchDownloadAsync(illust))
                {
                    if (item.IsSuccess) completedCount++;
                    else failedList.Add(item.Illust);
                    ClearConsoleLine(cursorTop);
                    Console.Write($"Downloaded {completedCount}/{illust.Count()} illusts.");
                }
                Console.WriteLine();
                return failedList;
            }
        }

        private static async IAsyncEnumerable<(Illust Illust, bool IsSuccess)> BatchDownloadAsync(IEnumerable<Illust> illust)
        {
            var tasks = illust.Select(it => SingleDownloadAsync(it)).ToList();
            while (tasks.Count > 0)
            {
                var completedTask = await Task.WhenAny(tasks);
                tasks.Remove(completedTask);
                yield return completedTask.Result;
            }
        }

        private static async Task<(Illust Illust, bool IsSuccess)> SingleDownloadAsync(Illust illust)
        {
            await _downloadSemaphore.WaitAsync();
            if (illust.IsAnimated)
            {
                var filename = Path.Combine(_directory, illust.Id + ".zip");
                if (File.Exists(filename))
                {
                    return (illust, true);
                }
                var tempFilename = filename + ".temp";
                var animatedDetail = await illust.GetAnimatedDetailAsync();
                var zipResponse = await animatedDetail.GetZipAsync();
                using (var file = File.OpenWrite(tempFilename))
                {
                    await zipResponse.Content.CopyToAsync(file);
                }
                File.Move(tempFilename, filename);
            }
            else
            {
                foreach (var page in illust.Pages)
                {
                    var filename = Path.Combine(_directory, GetFilename(page));
                    if (File.Exists(filename))
                    {
                        return (illust, true);
                    }
                    var tempFilename = filename + ".temp";
                    var response = await _apiClient.GetImageAsync(page.Original.Uri);
                    using (var file = File.OpenWrite(tempFilename))
                    {
                        await response.Content.CopyToAsync(file);
                    }
                    File.Move(tempFilename, filename);
                }
            }
            _downloadSemaphore.Release();
            return (illust, true);



            string GetFilename(IllustPage illustPage)
            {
                Path.GetInvalidFileNameChars();
                var filename = $"{Path.GetFileNameWithoutExtension(illustPage.Original.Uri.OriginalString)}_{illustPage.Illust.Title}{Path.GetExtension(illustPage.Original.Uri.OriginalString)}";
                foreach (var item in Path.GetInvalidFileNameChars())
                {
                    filename = filename.Replace(item, '_');
                }
                return filename;
            }
        }

        #region DownloadDirectory
        private const string DownloadDirectory = "pd_download_directory";

        private static void SetTargetDirectory()
        {
            if (File.Exists(DownloadDirectory))
            {
                _directory = File.ReadAllText(DownloadDirectory);
                if (Directory.Exists(_directory))
                {
                    Console.WriteLine($"Current download directory: {_directory}");
                    return;
                }
            }
            Console.WriteLine("Please set download directory:");
            _directory = Console.ReadLine();
            if (!Directory.Exists(_directory))
            {
                Directory.CreateDirectory(_directory);
            }
            var tempFile = Path.Combine(_directory, ".test_permission");
            File.Create(tempFile).Dispose();
            File.Delete(tempFile);
            File.WriteAllText(DownloadDirectory, _directory);
            SetTargetDirectory();
        }
        #endregion

        #region Auth
        private const string RefreshTokenFile = "pd_refresh_token";

        private static async Task Auth()
        {
            if (File.Exists(RefreshTokenFile))
            {
                Console.WriteLine("Read refresh token from saved file.");
                await _client.LoginAsync(File.ReadAllText(RefreshTokenFile));
                return;
            }
            Console.WriteLine(
@"Welcome to PixivDownloader!
Please select auth method:
1. Refresh Token
2. Web Browser");
            ConsoleKey? key = null;
            while (key == null)
            {
                key = Console.ReadKey().Key;
                switch (key)
                {
                    case ConsoleKey.D1:
                        ClearConsoleLine();
                        Console.Write("Saved refresh token: ");
                        var refreshToken = Console.ReadLine();
                        File.WriteAllText(RefreshTokenFile, await _client.LoginAsync(refreshToken));
                        break;
                    case ConsoleKey.D2:
                        ClearConsoleLine();
                        Console.WriteLine("WebBrownser login...");
                        File.WriteAllText(RefreshTokenFile, await _client.LoginAsync(AuthRequestFunc));

                        async Task<Uri> AuthRequestFunc(string url)
                        {
                            if (OperatingSystem.IsWindows())
                            {
                                RegisterUriScheme();
                            }
                            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo()
                            {
                                FileName = url,
                                UseShellExecute = true, // Be sure to set this to true (not the default value on .NET Core)
                            });
                            string redirectUrl;
                            if (OperatingSystem.IsWindows())
                            {
                                Console.WriteLine("Waiting for redirecting...");
                                redirectUrl = await WaitAndReadTempFile();
                                UnregisterUriScheme();
                            }
                            else
                            {
                                Console.WriteLine("Pixiv protocal url: ");
                                redirectUrl = Console.ReadLine();
                            }
                            return new Uri(redirectUrl);
                        }
                        break;
                    default:
                        key = null;
                        ClearConsoleLine();
                        break;
                }
            }
        }
        #endregion

        #region Proxy
        private const string ProxyFile = "pd_proxy";

        private static void SetProxy()
        {
            string proxyAddress;
            if (File.Exists(ProxyFile))
            {
                proxyAddress = File.ReadAllText(ProxyFile);
                if (!string.IsNullOrWhiteSpace(proxyAddress))
                {
                    _webProxy = new WebProxy(proxyAddress);
                    Console.WriteLine($"Current proxy: {proxyAddress}");
                }
                return;
            }
            Console.WriteLine("Please set proxy or empty to use system proxy:");
            proxyAddress = Console.ReadLine();
            File.WriteAllText(ProxyFile, proxyAddress);
            SetProxy();
        }
        #endregion

        #region Console
        private static void ClearConsoleLine(int cursorTop = -1)
        {
            if (cursorTop < 0)
            {
                cursorTop = Console.CursorTop;
            }
            Console.SetCursorPosition(0, cursorTop);
            Console.Write(new string(' ', Console.WindowWidth));
            Console.SetCursorPosition(0, cursorTop);
        }

        private static void WriteOneColorLine(string value, ConsoleColor consoleColor)
        {
            var color = Console.ForegroundColor;
            Console.ForegroundColor = consoleColor;
            Console.WriteLine(value);
            Console.ForegroundColor = color;
        }
        #endregion

        #region Windows
        #region Registry
        private const string UriScheme = "pixiv";

        [SupportedOSPlatform("windows")]
        private static void RegisterUriScheme()
        {
            using (var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey("SOFTWARE\\Classes\\" + UriScheme))
            {
                // Replace typeof(App) by the class that contains the Main method or any class located in the project that produces the exe.
                // or replace typeof(App).Assembly.Location by anything that gives the full path to the exe
                // https://docs.microsoft.com/en-us/dotnet/api/system.reflection.assembly.location?view=net-6.0
                // In .NET 5 and later versions, for bundled assemblies, the value returned is an empty string.
                string applicationLocation = typeof(Program).Assembly.Location;
                if (applicationLocation.EndsWith(".dll", StringComparison.InvariantCultureIgnoreCase))
                {
                    applicationLocation = Path.ChangeExtension(applicationLocation, "exe");
                }
                else if (string.IsNullOrEmpty(applicationLocation))
                {
                    applicationLocation = Process.GetCurrentProcess().MainModule.FileName;
                }
                if (!File.Exists(applicationLocation))
                {
                    throw new InvalidOperationException();
                }
                key.SetValue("", "URL:" + UriScheme);
                key.SetValue("URL Protocol", "");

                using (var defaultIcon = key.CreateSubKey("DefaultIcon"))
                {
                    defaultIcon.SetValue("", applicationLocation + ",1");
                }

                using (var commandKey = key.CreateSubKey(@"shell\open\command"))
                {
                    commandKey.SetValue("", "\"" + applicationLocation + "\" \"%1\"");
                }
            }
        }
        [SupportedOSPlatform("windows")]
        private static void UnregisterUriScheme()
        {
            Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree("SOFTWARE\\Classes\\" + UriScheme);
        }
        #endregion

        #region Share
        private const string TempShareFile = "./.temp_pixiv_auth";

        private static void WriteTempFile(string url)
        {
            File.WriteAllText(TempShareFile, url);
        }

        private static async Task<string> WaitAndReadTempFile()
        {
            int count = 55;
            while (count > 0)
            {
                await Task.Delay(TimeSpan.FromSeconds(1));
                if (File.Exists(TempShareFile))
                {
                    var url = File.ReadAllText(TempShareFile);
                    File.Delete(TempShareFile);
                    return url;
                }
                count--;
            }
            return string.Empty;
        }
        #endregion
        #endregion
    }
}
