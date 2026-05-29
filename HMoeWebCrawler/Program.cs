using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using HMoeData.Models;
using HMoeData.Persistence;
using HMoeWebCrawler;
using HMoeWebCrawler.LocalModels;

// 连续获取到n个已存在的项目后，停止爬取
const int continuousExistenceThreshold = 10;
Settings? settings = null;

// 记录日志路径
var loggerPath =
#if DEBUG
    @"D:\HMoeWebCrawler";
#else
    Environment.CurrentDirectory;
#endif
var loggerImgPath = Path.Combine(loggerPath, "img");
var loggerDbPath = Path.Combine(loggerPath, "current.db");
var loggerLastDbPath = Path.Combine(loggerPath, "last.db");
var loggerSettingsPath = Path.Combine(loggerPath, "settings.json");

_ = Directory.CreateDirectory(loggerImgPath);

if (!File.Exists(loggerSettingsPath))
    throw new("Missing Settings in " + loggerSettingsPath);

try
{
    settings = await JsonSerializer.OpenDeserializeAsync(loggerSettingsPath, SerializerContext.DefaultOverride.Settings);
}
catch (Exception e)
{
    WriteException(e);
}

if (settings is null)
    throw new InvalidDataException("Invalid settings " + loggerSettingsPath);

await using var session = new HMoeSession();
await session.InitAsync();
await session.NavigateToSiteAsync();
await session.EnsureLoggedInAsync(settings.Email, settings.Password);
await session.FetchNonceAsync(); // 获取 nonce 并签到

using var postLookup = HMoeDbStore.OpenPostLookup(loggerDbPath);
var postsToSave = new List<Post>();
var latestBatchPostsCount = 0;

if (!settings.NewSession)
    foreach (var post in HMoeDbStore.LoadNewPosts(loggerDbPath))
    {
        latestBatchPostsCount++;
        session.DownloadThumbnailAddToList(post, loggerImgPath);
    }

var newItemsCount = 0;
var continuousExistence = 0;
var data = new SearchData(1);
while (true)
{
    var tempPosts = await session.SearchPageAsync(data);

    while (tempPosts.TryPop(out var post))
        if (!postLookup.Exists(post.Id) && postsToSave.All(existingPost => existingPost.Id != post.Id))
        {
            postsToSave.Add(post);
            Console.WriteLine($"New Item [{post.Id}]: {post.Url}");
            newItemsCount++;
            if (continuousExistence < continuousExistenceThreshold)
                continuousExistence = 0;
            session.DownloadThumbnailAddToList(post, loggerImgPath);
        }
        else
        {
            Console.WriteLine($"Item existed: {post.Id} Continuous existence count: {continuousExistence}");
            continuousExistence++;
        }

    if (continuousExistence >= continuousExistenceThreshold)
        break;

    data.Paged++;
}

Console.WriteLine("\e[32m达到连续存在阈值，停止爬取。等待缩略图下载完成\e[0m");

await session.WhenAllDownloadAsync();

if (newItemsCount is 0)
{
    Console.WriteLine("没有新项目，不保存");
}
else
{
    var resultPosts = postsToSave.OrderByDescending(t => t.Date).ToList();
    var writeTime = DateTimeOffset.UtcNow;
    var currentBatchCount = settings.NewSession ? newItemsCount : latestBatchPostsCount + newItemsCount;
    Console.WriteLine($"\e[32m本次写入批次 {currentBatchCount} 项，新抓取 {newItemsCount} 项\e[0m");

    try
    {
        Console.WriteLine("Saving " + loggerDbPath);

        if (File.Exists(loggerDbPath))
            File.Copy(loggerDbPath, loggerLastDbPath, true);

        HMoeDbStore.SavePosts(loggerDbPath, resultPosts, new(writeTime, !settings.NewSession));
    }
    catch (Exception e)
    {
        WriteException(e);
        var fileName = $"TempLog {DateTime.Now:yyyy.MM.dd HH-mm-ss}.db";
        Console.WriteLine($"\e[31m保存失败，备份到 {fileName}\e[0m");
        var loggerTempDbPath = Path.Combine(loggerPath, fileName);
        HMoeDbStore.SavePosts(loggerTempDbPath, resultPosts, new(writeTime, !settings.NewSession));
    }
}

Console.ReadKey();

return;

static void WriteException(Exception e) => Console.WriteLine($"\e[90m{e.Message}\e[0m");
