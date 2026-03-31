using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using HMoeWebCrawler;
using HMoeWebCrawler.LocalModels;
using HMoeWebCrawler.Models;

// 连续获取到n个已存在的项目后，停止爬取
const int continuousExistenceThreshold = 5;
Settings? settings = null;

// 记录日志路径
var loggerPath =
#if DEBUG
    @"D:\HMoeWebCrawler";
#else
    Environment.CurrentDirectory;
#endif
var loggerImgPath = Path.Combine(loggerPath, "img");
var loggerJsonPath = Path.Combine(loggerPath, "current.json");
var loggerLastJsonPath = Path.Combine(loggerPath, "last.json");
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

HashSet<Post>? postSet = null;

if (File.Exists(loggerJsonPath)
    && await JsonSerializer.OpenDeserializeAsync(loggerJsonPath, SerializerContext.DefaultOverride.HashSetPost) is { } r)
{
    postSet = r;
    foreach (var post in postSet)
    {
        session.DownloadThumbnailAddToList(post, loggerImgPath);
        if (settings.NewSession)
            post.IsNew = false;
    }   
}

postSet ??= [];

var newItemsCount = 0;
var continuousExistence = 0;
var data = new SearchData(1);
while (true)
{
    var tempPosts = await session.SearchPageAsync(data);

    while (tempPosts.TryPop(out var post))
        if (postSet.Add(post))
        {
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
    var resultPosts = postSet.OrderByDescending(t => t.Date).ToList();
    Console.Write("\e[32m获取 ");
    var allPostsCount = settings.NewSession
        ? newItemsCount
        : resultPosts.Count(t => t.IsNew);
    if (!settings.NewSession)
        Console.Write(allPostsCount - newItemsCount + " + ");
    Console.WriteLine($"{newItemsCount} 个新项目\e[0m");

    var myList = resultPosts.Take(allPostsCount + (continuousExistenceThreshold * 4)).ToArray();

    try
    {
        if (File.Exists(loggerJsonPath))
            File.Move(loggerJsonPath, loggerLastJsonPath, true);

        Console.WriteLine("Saving " + loggerJsonPath);
        await JsonSerializer.CreateSerializeAsync(loggerJsonPath, myList, SerializerContext.DefaultOverride.IReadOnlyListPost);
    }
    catch (Exception e)
    {
        WriteException(e);
        var fileName = $"TempLog {DateTime.Now:yyyy.MM.dd HH-mm-ss}.json";
        Console.WriteLine($"\e[31m保存失败，备份到 {fileName}\e[0m");
        var loggerTempJsonPath = Path.Combine(loggerPath, fileName);
        await JsonSerializer.CreateSerializeAsync(loggerTempJsonPath, myList, SerializerContext.DefaultOverride.IReadOnlyListPost);
    }
}

Console.ReadKey();

return;

static void WriteException(Exception e) => Console.WriteLine($"\e[90m{e.Message}\e[0m");
