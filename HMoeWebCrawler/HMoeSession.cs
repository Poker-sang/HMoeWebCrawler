using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using HMoeWebCrawler.Models;
using Microsoft.Playwright;

namespace HMoeWebCrawler;

public class HMoeSession : IAsyncDisposable
{
    public const string Domain = "https://www.mhh1.com/";

    /// <summary>
    /// 最大请求间隔，超过后中断
    /// </summary>
    public TimeSpan CoolDownThreshold { get; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// 每次请求时间间隔
    /// </summary>
    public TimeSpan DefaultCoolDown = TimeSpan.FromSeconds(3);

    private readonly List<Task> _imageDownloadTasks = [];

    private IPlaywright? _playwright;
    private IBrowserContext? _browserContext;
    private IPage? _page;
    private readonly SemaphoreSlim _downloadSemaphore = new(4);
    private string? _loginNonce;
    private DateTime _lastRequest = DateTime.MinValue;

    public IReadOnlyList<Task> ImageDownloadTasks => _imageDownloadTasks;

    public async Task InitAsync()
    {
        _playwright = await Playwright.CreateAsync();

        var userDataDir = Path.Combine(
#if DEBUG
            @"D:\HMoeWebCrawler",
#else
            Environment.CurrentDirectory,
#endif
            "browser-data");

        Directory.CreateDirectory(userDataDir);

        _browserContext = await _playwright.Chromium.LaunchPersistentContextAsync(
            userDataDir,
            new()
            {
                Headless = false,
                Channel = "msedge",
                ViewportSize = new() { Width = 1280, Height = 800 },
                UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/143.0.0.0 Safari/537.36 Edg/143.0.0.0"
            });

        _page = _browserContext.Pages.FirstOrDefault() ?? await _browserContext.NewPageAsync();

        // 隐藏 webdriver 标识以避免被检测
        await _page.AddInitScriptAsync("Object.defineProperty(navigator, 'webdriver', {get: () => undefined})");
    }

    public async Task NavigateToSiteAsync()
    {
        Console.WriteLine("正在打开网站...");
        var response = await _page!.GotoAsync(Domain, new() { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 60000 });

        // 等待5秒挑战或其他加载完成
        try
        {
            await _page.WaitForLoadStateAsync(LoadState.NetworkIdle, new() { Timeout = 30000 });
        }
        catch (TimeoutException)
        {
            Console.WriteLine("NetworkIdle 超时，继续...");
        }

        // 如果页面标题包含 challenge 关键字，等待跳转完成
        var title = await _page.TitleAsync();
        if (title.Contains("moment", StringComparison.OrdinalIgnoreCase)
            || title.Contains("check", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine("检测到挑战页面，等待自动跳转...");
            try
            {
                await _page.WaitForURLAsync($"{Domain}**", new() { Timeout = 30000 });
                await _page.WaitForLoadStateAsync(LoadState.NetworkIdle, new() { Timeout = 30000 });
            }
            catch (TimeoutException)
            {
                Console.WriteLine("挑战页面等待超时，继续...");
            }
        }

        Console.WriteLine($"\e[32m网站已打开: {_page.Url}\e[0m");
    }

    public async Task EnsureLoggedInAsync(string email, string password)
    {
        var cookies = await _browserContext!.CookiesAsync([Domain]);
        var isLoggedIn = cookies.Any(c => c.Name.StartsWith("wordpress_logged_in"));

        if (isLoggedIn)
        {
            Console.WriteLine("\e[32m已登录\e[0m");
            return;
        }

        Console.WriteLine("未登录，正在登录...");

        var nonce = await FetchNonceFromPageAsync();

        // 获取验证码
        var captchaJson = await PageFetchAsync(
            $"/wp-admin/admin-ajax.php?_nonce={nonce}&action=b9215121b88d889ea28808c5adabbbf5&type=getCaptcha");

        var captchaResponse = JsonSerializer.Deserialize(captchaJson, SerializerContext.Default.ApiResponse)
                              ?? throw new InvalidOperationException("Failed to deserialize captcha response.");
        var captchaData = captchaResponse.GetData(SerializerContext.Default.ImageDataResult);
        var base64 = captchaData.ImgData;

        // 在浏览器中显示验证码
        await _page!.EvaluateAsync(@"(imgSrc) => {
            const div = document.createElement('div');
            div.id = 'captcha-overlay';
            div.style.cssText = 'position:fixed;top:0;left:0;width:100%;height:100%;background:rgba(0,0,0,0.8);z-index:99999;display:flex;align-items:center;justify-content:center;flex-direction:column;';
            const img = document.createElement('img');
            img.src = imgSrc;
            img.style.cssText = 'max-width:400px;background:white;padding:10px;border-radius:8px;';
            div.appendChild(img);
            const text = document.createElement('p');
            text.textContent = '请在控制台输入验证码';
            text.style.cssText = 'color:white;font-size:20px;margin-top:20px;';
            div.appendChild(text);
            document.body.appendChild(div);
        }", base64);

        string? captcha;
        do
        {
            Console.Write("请输入验证码: ");
            captcha = Console.ReadLine();
        } while (string.IsNullOrWhiteSpace(captcha));

        await _page.EvaluateAsync("() => document.getElementById('captcha-overlay')?.remove()");

        // 提交登录（使用参数化调用防止注入）
        var loginJson = await _page.EvaluateAsync<string>(@"
            async ([nonce, email, pwd, captcha]) => {
                const url = `/wp-admin/admin-ajax.php?_nonce=${nonce}&action=0ac2206cd584f32fba03df08b4123264&type=login`;
                const formData = new URLSearchParams();
                formData.append('email', email);
                formData.append('pwd', pwd);
                formData.append('captcha', captcha);
                formData.append('type', 'login');
                const response = await fetch(url, {
                    method: 'POST',
                    headers: { 'X-Requested-With': 'XMLHttpRequest', 'Content-Type': 'application/x-www-form-urlencoded' },
                    body: formData.toString()
                });
                return await response.text();
            }
        ", new object[] { nonce, email, password, captcha });

        Console.WriteLine("登录响应: " + loginJson);

        await _page.ReloadAsync(new() { WaitUntil = WaitUntilState.NetworkIdle });
        Console.WriteLine("\e[32m登录完成\e[0m");
    }

    public async Task<string> FetchNonceAsync()
    {
        var nonce = await FetchNonceFromPageAsync();
        _loginNonce = nonce;
        _ = await SignAsync(nonce);
        return nonce;
    }

    private async Task<string> FetchNonceFromPageAsync()
    {
        Console.WriteLine("正在获取 nonce...");
        var nonceJson = await PageFetchAsync(
            "/wp-admin/admin-ajax.php?action=285d6af5ed069e78e04b2d054182dcb5&d6ca819426678dab7a26ecb2802d8aec%5Btype%5D=checkUnread&6f05c9bced69c22452fcd115e6fc4838%5Btype%5D=getHomepagePosts");

        using var jsonDocument = JsonDocument.Parse(nonceJson);
        var nonce = jsonDocument.RootElement.GetProperty("_nonce").GetString()
                    ?? throw new InvalidOperationException("Nonce not found in response.");
        Console.WriteLine("获取 nonce: " + nonce);
        return nonce;
    }

    public async Task<bool> SignAsync(string nonce)
    {
        Console.WriteLine("正在签到...");
        var signJson = await PageFetchAsync(
            $"/wp-admin/admin-ajax.php?_nonce={nonce}&action=9f9fa05823795c1c74e8c27e8d5e6930&type=goSign");

        var response = JsonSerializer.Deserialize(signJson, SerializerContext.Default.ApiResponse)
                       ?? throw new InvalidOperationException("Failed to deserialize sign response.");
        var status = response.Code is 0;
        Console.WriteLine($"签到{(status ? "成功" : "失败")}: {response.Message}");
        return status;
    }

    public async Task<Stack<Post>> SearchPageAsync(SearchData data)
    {
        var coolDown = DefaultCoolDown;

        while (true)
            try
            {
                if (_loginNonce is not null)
                {
                    while (DateTime.UtcNow < _lastRequest + coolDown)
                        await Task.Delay(500);

                    var query = data.Encode();
                    Console.WriteLine("正在下载第 " + data.Paged + " 页");

                    var searchJson = await PageFetchAsync(
                        $"/wp-admin/admin-ajax.php?_nonce={_loginNonce}&action=b9338a11fcc41c1ed5447625d1c0e743&query={query}");

                    var result = JsonSerializer.Deserialize(searchJson, SerializerContext.DefaultOverride.ApiResponse)
                                 ?? throw new InvalidOperationException("Failed to deserialize search response.");

                    if (result.Code is not 10007)
                    {
                        var r = result.GetData(SerializerContext.Default.PostsSearchResult);
                        Console.WriteLine("已下载第 " + data.Paged + " 页");
                        _lastRequest = DateTime.UtcNow;
                        return r.Posts;
                    }
                }

                // 会话过期，刷新页面重新获取凭据
                Console.WriteLine("会话过期，正在刷新...");
                await _page!.ReloadAsync(new() { WaitUntil = WaitUntilState.NetworkIdle });
                _loginNonce = await FetchNonceFromPageAsync();
            }
            catch (Exception e)
            {
                WriteException(e);
                if (coolDown > CoolDownThreshold)
                    throw;
                coolDown *= 2;
                await Task.Delay(coolDown);
            }
    }

    /// <summary>
    /// 通过浏览器页面上下文执行 fetch 请求，自动携带 cookies 和过挑战
    /// </summary>
    private async Task<string> PageFetchAsync(string relativeUrl)
    {
        return await _page!.EvaluateAsync<string>(@"
            async (url) => {
                const response = await fetch(url, { headers: { 'X-Requested-With': 'XMLHttpRequest' } });
                return await response.text();
            }
        ", relativeUrl);
    }

    public Task WhenAllDownloadAsync() => Task.WhenAll(_imageDownloadTasks);

    public void DownloadThumbnailAddToList(Post post, string imagePath)
    {
        _imageDownloadTasks.Add(DownloadThumbnailAsync(post, imagePath));
    }

    public async Task DownloadThumbnailAsync(Post post, string imagePath)
    {
        var postThumbnailUrl = post.Thumbnail.Url;

        // 处理相对 URI
        if (!postThumbnailUrl.IsAbsoluteUri)
        {
            var originalString = postThumbnailUrl.OriginalString;
            postThumbnailUrl = originalString.StartsWith("//")
                ? new Uri("https:" + originalString)
                : new(new(Domain), postThumbnailUrl);
        }

        var fileName = post.ThumbnailFileName;
        var imgPath = Path.Combine(imagePath, post.ThumbnailFileName);
        if (File.Exists(imgPath))
            return;
        await _downloadSemaphore.WaitAsync();
        try
        {
            // 在浏览器页面内通过 fetch 下载，自动带上 Referer/Cookie 等所有头，避免 CDN 防盗链 403
            var base64 = await _page!.EvaluateAsync<string?>(@"async (url) => {
                try {
                    const resp = await fetch(url, { referrer: 'https://www.mhh1.com/', credentials: 'include' });
                    if (!resp.ok) return null;
                    const buf = await resp.arrayBuffer();
                    const bytes = new Uint8Array(buf);
                    let binary = '';
                    for (let i = 0; i < bytes.length; i++) binary += String.fromCharCode(bytes[i]);
                    return btoa(binary);
                } catch { return null; }
            }", postThumbnailUrl.AbsoluteUri);

            if (base64 is not null)
            {
                var body = Convert.FromBase64String(base64);
                await File.WriteAllBytesAsync(imgPath, body);
                Console.WriteLine("Downloaded thumbnail " + fileName);
            }
            else
            {
                Console.WriteLine($"Download thumbnail failed [{post.Id}]: {postThumbnailUrl}");
            }
        }
        catch (Exception e)
        {
            WriteException(e);
            Console.WriteLine($"Download thumbnail failed [{post.Id}]: {postThumbnailUrl} ({post.Url})");
            if (File.Exists(imgPath))
                File.Delete(imgPath);
        }
        finally
        {
            _downloadSemaphore.Release();
        }
    }

    private static void WriteException(Exception e) => Console.WriteLine($"\e[90m{e.Message}\e[0m");

    public async ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);
        _downloadSemaphore.Dispose();
        if (_browserContext is not null)
            await _browserContext.CloseAsync();
        _playwright?.Dispose();
    }
}
