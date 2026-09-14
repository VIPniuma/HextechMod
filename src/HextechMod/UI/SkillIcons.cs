using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace PeakModder.HextechMod;

/// <summary>
/// 加载嵌入 dll 中的技能图标。
/// 图标来自开源图标库 game-icons.net（经 Iconify 分发，CC BY 3.0）。
/// </summary>
internal static class SkillIcons
{
    private static readonly Dictionary<string, Texture2D?> Cache = new();

    public static Texture2D? Get(SkillId id) => Get(ResourceName(id));

    public static Texture2D? Get(string resourceName)
    {
        if (Cache.TryGetValue(resourceName, out var cached))
        {
            return cached;
        }

        var texture = LoadFor(resourceName);
        Cache[resourceName] = texture;
        return texture;
    }

    private static Texture2D? LoadFor(string resourceName)
    {
        var assembly = Assembly.GetExecutingAssembly();
        var suffix = $".{resourceName}.png";
        var name = assembly.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));

        if (name == null)
        {
            HextechPlugin.Log.LogWarning($"找不到技能图标资源：{suffix}");
            return null;
        }

        try
        {
            using var stream = assembly.GetManifestResourceStream(name);
            if (stream == null)
            {
                return null;
            }

            var bytes = new byte[stream.Length];
            _ = stream.Read(bytes, 0, bytes.Length);

            var texture = new Texture2D(2, 2);
            if (!texture.LoadImage(bytes))
            {
                HextechPlugin.Log.LogWarning($"技能图标 {resourceName} 加载失败：PNG 解析出错");
                UnityEngine.Object.Destroy(texture);
                return null;
            }

            texture.wrapMode = TextureWrapMode.Clamp;
            texture.filterMode = FilterMode.Bilinear;
            return texture;
        }
        catch (Exception exception)
        {
            HextechPlugin.Log.LogWarning($"技能图标 {resourceName} 加载失败：{exception.Message}");
            return null;
        }
    }

    private static string ResourceName(SkillId id) => id switch
    {
        SkillId.SuperJump => "super-jump",
        SkillId.Sprint => "sprint",
        SkillId.Cleanse => "cleanse",
        SkillId.Heal => "heal",
        SkillId.Adrenaline => "adrenaline",
        SkillId.Invincible => "invincible",
        _ => id.ToString().ToLowerInvariant(),
    };
}
