using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using MyMediaPlayer.Models;

namespace MyMediaPlayer.Services;

/// <summary>
/// 播放目录持久化服务：将目录树以 JSON 形式保存到 %AppData%\MyMediaPlayer\playlist.json。
/// 只存储目录名称与原视频的完整路径。
/// </summary>
public class PlaylistService
{
    private sealed class NodeDto
    {
        public string Name { get; set; } = string.Empty;
        public bool IsFolder { get; set; }
        public string? FilePath { get; set; }
        public bool IsExpanded { get; set; } = true;
        public List<NodeDto>? Children { get; set; }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public string StorageFilePath { get; }

    public PlaylistService()
    {
        string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "vvPlayer");
        string legacyDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MyMediaPlayer");
        Directory.CreateDirectory(dir);
        StorageFilePath = Path.Combine(dir, "playlist.json");

        // 若历史目录下存在旧播放列表且新目录尚无文件，自动无缝迁移
        try
        {
            string legacyFile = Path.Combine(legacyDir, "playlist.json");
            if (!File.Exists(StorageFilePath) && File.Exists(legacyFile))
            {
                File.Copy(legacyFile, StorageFilePath, true);
            }
        }
        catch
        {
        }
    }

    public ObservableCollection<PlaylistNode> Load()
    {
        var roots = new ObservableCollection<PlaylistNode>();
        if (!File.Exists(StorageFilePath))
        {
            return roots;
        }

        try
        {
            string json = File.ReadAllText(StorageFilePath, Encoding.UTF8);
            var dtos = JsonSerializer.Deserialize<List<NodeDto>>(json, JsonOptions);
            if (dtos != null)
            {
                foreach (var dto in dtos)
                {
                    roots.Add(FromDto(dto, null));
                }
            }
        }
        catch (Exception ex)
        {
            // 文件损坏时备份原文件，避免下次保存时覆盖用户数据
            Debug.WriteLine($"[PlaylistService] 读取播放目录失败: {ex.Message}");
            try
            {
                File.Copy(StorageFilePath, StorageFilePath + $".broken_{DateTime.Now:yyyyMMddHHmmss}", overwrite: true);
            }
            catch
            {
                // 忽略备份异常
            }
        }

        return roots;
    }

    public void Save(IEnumerable<PlaylistNode> roots)
    {
        try
        {
            var dtos = new List<NodeDto>();
            foreach (var node in roots)
            {
                dtos.Add(ToDto(node));
            }

            string json = JsonSerializer.Serialize(dtos, JsonOptions);
            string tempPath = StorageFilePath + ".tmp";
            File.WriteAllText(tempPath, json, Encoding.UTF8);
            File.Move(tempPath, StorageFilePath, overwrite: true);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[PlaylistService] 保存播放目录失败: {ex.Message}");
        }
    }

    private static PlaylistNode FromDto(NodeDto dto, PlaylistNode? parent)
    {
        var node = new PlaylistNode
        {
            Name = dto.Name,
            IsFolder = dto.IsFolder,
            FilePath = dto.FilePath,
            IsExpanded = dto.IsExpanded,
            Parent = parent
        };

        if (dto.IsFolder && dto.Children != null)
        {
            foreach (var childDto in dto.Children)
            {
                node.Children.Add(FromDto(childDto, node));
            }
        }

        return node;
    }

    private static NodeDto ToDto(PlaylistNode node)
    {
        var dto = new NodeDto
        {
            Name = node.Name,
            IsFolder = node.IsFolder,
            FilePath = node.FilePath,
            IsExpanded = node.IsExpanded
        };

        if (node.IsFolder)
        {
            dto.Children = new List<NodeDto>();
            foreach (var child in node.Children)
            {
                dto.Children.Add(ToDto(child));
            }
        }

        return dto;
    }
}
