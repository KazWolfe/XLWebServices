﻿using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Prometheus;
using XLWebServices.Services;
using XLWebServices.Services.PluginData;

namespace XLWebServices.Controllers;

[ApiController]
[Route("[controller]/[action]")]
public class PluginController : ControllerBase
{
    private readonly ILogger<PluginController> _logger;
    private readonly RedisService _redis;
    private readonly IConfiguration _configuration;
    private readonly PluginDataService _pluginData;
    private readonly FileCacheService _cache;

    private static readonly Counter DownloadsOverTime = Metrics.CreateCounter("xl_plugindl", "XIVLauncher Plugin Downloads", "Name", "Testing");

    private const string RedisCumulativeKey = "XLPluginDlCumulative";

    public PluginController(ILogger<PluginController> logger, RedisService redis, IConfiguration configuration, PluginDataService pluginData, FileCacheService cache)
    {
        _logger = logger;
        _redis = redis;
        _configuration = configuration;
        _pluginData = pluginData;
        _cache = cache;
    }

    [HttpGet("{internalName}")]
    public async Task<IActionResult> Download(string internalName, [FromQuery(Name = "branch")] string branch = "master", [FromQuery(Name = "isTesting")] bool isTesting = false)
    {
        var manifest = this._pluginData.PluginMaster!.FirstOrDefault(x => x.InternalName == internalName);
        if (manifest == null)
            return BadRequest("Invalid plugin");

        DownloadsOverTime.WithLabels(internalName.ToLower(), isTesting.ToString()).Inc();

        await _redis.IncrementCount(internalName);
        await _redis.IncrementCount(RedisCumulativeKey);

        const string githubPath = "https://raw.githubusercontent.com/goatcorp/DalamudPlugins/{0}/{1}/{2}/latest.zip";
        var baseUrl = isTesting ? "testing" : "plugins";
        var cachedFile = await this._cache.CacheFile(internalName, manifest.AssemblyVersion.ToString(),
            string.Format(githubPath, branch, baseUrl, internalName), FileCacheService.CachedFile.FileCategory.Plugin);

        return new RedirectResult($"{this._configuration["HostedUrl"]}/File/Get/{cachedFile.FileId}");
    }

    [HttpGet]
    public async Task<Dictionary<string, long>> DownloadCounts()
    {
        var counts = new Dictionary<string, long>();
        foreach (var plugin in _pluginData.PluginMaster!)
        {
            counts.Add(plugin.InternalName, await _redis.GetCount(plugin.InternalName));
        }

        return counts;
    }

    [HttpGet]
    public IActionResult PluginMaster()
    {
        return Content(JsonSerializer.Serialize(this._pluginData.PluginMaster, new JsonSerializerOptions
        {
            WriteIndented = true,
        }), "application/json");
    }

    [HttpGet("{internalName}")]
    public IActionResult Plugin(string internalName)
    {
        var plugin = _pluginData.PluginMaster!.FirstOrDefault(x => x.InternalName == internalName);
        if (plugin == null)
            return BadRequest("Invalid plugin");

        return Content(JsonSerializer.Serialize(plugin, new JsonSerializerOptions
        {
            WriteIndented = true,
        }), "application/json");
    }

    [HttpGet]
    public async Task<IActionResult> ClearCache([FromQuery] string key)
    {
        if (key != _configuration["CacheClearKey"])
            return BadRequest();

        await _pluginData.ClearCache();

        return Ok();
    }

    [HttpGet]
    public async Task<PluginMeta> Meta()
    {
        return new PluginMeta
        {
            NumPlugins = _pluginData.PluginMaster!.Count,
            LastUpdate = _pluginData.LastUpdate,
            CumulativeDownloads = await _redis.GetCount(RedisCumulativeKey),
        };
    }

    [HttpGet]
    public IReadOnlyList<PluginDataService.DalamudChangelog> CoreChangelog()
    {
        return _pluginData.DalamudChangelogs;
    }

    public class PluginMeta
    {
        public int NumPlugins { get; init; }
        public long CumulativeDownloads { get; init; }
        public DateTime LastUpdate { get; init; }
    }
}