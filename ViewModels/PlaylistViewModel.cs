using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using MyMediaPlayer.Models;
using MyMediaPlayer.Services;
using MyMediaPlayer.Views;

namespace MyMediaPlayer.ViewModels;

/// <summary>
/// 播放目录 ViewModel：管理用户自建的分类目录树（只保存原视频地址），支持新建/重命名/删除目录、
/// 添加视频、拖拽归类、双击播放，并自动持久化到 playlist.json。
/// </summary>
public partial class PlaylistViewModel : ObservableObject
{
    public static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mkv", ".avi", ".mov", ".wmv", ".flv", ".webm", ".m4v", ".ts", ".mpg", ".mpeg", ".3gp"
    };

    private readonly PlaylistService _playlistService = new();
    private readonly Action<string> _playRequested;
    private readonly Func<string?> _currentFileProvider;
    private string? _currentPlayingPath;

    public ObservableCollection<PlaylistNode> Roots { get; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RenameCommand))]
    [NotifyCanExecuteChangedFor(nameof(DeleteCommand))]
    [NotifyCanExecuteChangedFor(nameof(OpenFileLocationCommand))]
    [NotifyCanExecuteChangedFor(nameof(PlaySelectedCommand))]
    private PlaylistNode? _selectedNode;

    public bool IsEmpty => Roots.Count == 0;

    public PlaylistViewModel(Action<string> playRequested, Func<string?> currentFileProvider)
    {
        _playRequested = playRequested;
        _currentFileProvider = currentFileProvider;
        Roots = _playlistService.Load();
        Roots.CollectionChanged += (_, _) => OnPropertyChanged(nameof(IsEmpty));
    }

    // ───────────────────────── 命令 ─────────────────────────

    [RelayCommand]
    private void NewFolder()
    {
        string? name = InputDialog.Prompt("新建目录", "请输入目录名称：", "新建目录");
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        var parent = ResolveTargetFolder(SelectedNode);
        var folder = new PlaylistNode { Name = name, IsFolder = true };
        AddChild(parent, folder);
        if (parent != null)
        {
            parent.IsExpanded = true;
        }
        Select(folder);
        SaveAndRefresh();
    }

    [RelayCommand]
    private void AddVideos()
    {
        var dialog = new OpenFileDialog
        {
            Title = "添加视频到播放目录（仅记录视频地址）",
            Filter = "视频文件|*.mp4;*.mkv;*.avi;*.mov;*.wmv;*.flv;*.webm;*.m4v;*.ts;*.mpg;*.mpeg;*.3gp|所有文件|*.*",
            Multiselect = true
        };

        if (dialog.ShowDialog() == true)
        {
            AddPaths(dialog.FileNames, ResolveTargetFolder(SelectedNode));
        }
    }

    [RelayCommand]
    private void AddCurrentVideo()
    {
        string? current = _currentFileProvider();
        if (string.IsNullOrEmpty(current))
        {
            MessageBox.Show("当前没有正在播放的视频。", "播放目录", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        AddPaths(new[] { current }, ResolveTargetFolder(SelectedNode));
    }

    private bool HasSelection() => SelectedNode != null;

    private bool HasSelectedVideo() => SelectedNode is { IsFolder: false };

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void Rename()
    {
        var node = SelectedNode;
        if (node == null)
        {
            return;
        }

        string? newName = InputDialog.Prompt("重命名", node.IsFolder ? "目录名称：" : "显示名称（不会修改原视频文件名）：", node.Name);
        if (!string.IsNullOrWhiteSpace(newName) && newName != node.Name)
        {
            node.Name = newName;
            SaveAndRefresh();
        }
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void Delete()
    {
        var node = SelectedNode;
        if (node == null)
        {
            return;
        }

        if (node.IsFolder && node.Children.Count > 0)
        {
            var result = MessageBox.Show(
                $"确定删除目录「{node.Name}」及其中的 {node.CountVideos()} 个视频条目吗？\n\n（仅从播放目录中移除记录，不会删除硬盘上的原视频文件）",
                "删除目录",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);
            if (result != MessageBoxResult.Yes)
            {
                return;
            }
        }

        var siblings = node.Parent?.Children ?? Roots;
        siblings.Remove(node);
        node.Parent = null;
        SelectedNode = null;
        SaveAndRefresh();
    }

    [RelayCommand(CanExecute = nameof(HasSelectedVideo))]
    private void PlaySelected()
    {
        Play(SelectedNode);
    }

    [RelayCommand(CanExecute = nameof(HasSelectedVideo))]
    private void OpenFileLocation()
    {
        var node = SelectedNode;
        if (node?.FilePath == null)
        {
            return;
        }

        try
        {
            if (File.Exists(node.FilePath))
            {
                Process.Start("explorer.exe", $"/select,\"{node.FilePath}\"");
            }
            else
            {
                string? dir = Path.GetDirectoryName(node.FilePath);
                if (dir != null && Directory.Exists(dir))
                {
                    Process.Start("explorer.exe", $"\"{dir}\"");
                }
                else
                {
                    MessageBox.Show($"原视频及其所在文件夹均不存在：\n{node.FilePath}", "播放目录", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
        }
        catch
        {
            // 忽略资源管理器启动异常
        }
    }

    [RelayCommand]
    private void CheckMissingFiles()
    {
        foreach (var node in EnumerateAll(Roots))
        {
            node.RefreshDisplay();
        }
    }

    // ───────────────────────── 供视图调用的操作 ─────────────────────────

    /// <summary>播放某个视频条目</summary>
    public void Play(PlaylistNode? node)
    {
        if (node == null || node.IsFolder || string.IsNullOrEmpty(node.FilePath))
        {
            return;
        }

        if (!File.Exists(node.FilePath))
        {
            node.RefreshDisplay();
            MessageBox.Show($"原视频文件不存在（可能已被移动或删除）：\n{node.FilePath}", "播放目录", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _playRequested(node.FilePath);
    }

    /// <summary>添加文件/文件夹路径到目标目录（拖入的文件夹会自动创建同名分类目录）</summary>
    public void AddPaths(IEnumerable<string> paths, PlaylistNode? targetFolder)
    {
        PlaylistNode? lastAdded = null;
        int addedCount = 0;

        foreach (string path in paths)
        {
            if (Directory.Exists(path))
            {
                var folderNode = BuildFolderFromDirectory(path);
                if (folderNode != null)
                {
                    AddChild(targetFolder, folderNode);
                    lastAdded = folderNode;
                    addedCount += folderNode.CountVideos();
                }
            }
            else if (File.Exists(path) && VideoExtensions.Contains(Path.GetExtension(path)))
            {
                var siblings = targetFolder?.Children ?? Roots;
                var existing = siblings.FirstOrDefault(n => !n.IsFolder && string.Equals(n.FilePath, path, StringComparison.OrdinalIgnoreCase));
                if (existing != null)
                {
                    lastAdded = existing;
                    continue;
                }

                var videoNode = new PlaylistNode
                {
                    Name = Path.GetFileNameWithoutExtension(path),
                    IsFolder = false,
                    FilePath = Path.GetFullPath(path)
                };
                videoNode.IsPlaying = string.Equals(videoNode.FilePath, _currentPlayingPath, StringComparison.OrdinalIgnoreCase);
                AddChild(targetFolder, videoNode);
                lastAdded = videoNode;
                addedCount++;
            }
        }

        if (targetFolder != null)
        {
            targetFolder.IsExpanded = true;
        }

        if (lastAdded != null)
        {
            Select(lastAdded);
        }

        if (addedCount > 0 || lastAdded != null)
        {
            SaveAndRefresh();
        }
    }

    /// <summary>拖拽归类：把节点移动到目标目录（target 为 null 表示移到最外层）</summary>
    public bool MoveNode(PlaylistNode node, PlaylistNode? target)
    {
        // 拖到视频条目上时，等同于放入该视频所在目录
        var targetFolder = ResolveTargetFolder(target);

        if (ReferenceEquals(node.Parent, targetFolder))
        {
            return false;
        }

        // 不能把目录拖进它自己或它的子目录
        if (node.IsFolder && node.IsSelfOrAncestorOf(targetFolder))
        {
            return false;
        }

        var oldSiblings = node.Parent?.Children ?? Roots;
        oldSiblings.Remove(node);
        AddChild(targetFolder, node);
        if (targetFolder != null)
        {
            targetFolder.IsExpanded = true;
        }

        Select(node);
        SaveAndRefresh();
        return true;
    }

    /// <summary>主界面开始播放某个视频时调用，用于高亮播放目录中对应的条目</summary>
    public void MarkPlaying(string? filePath)
    {
        _currentPlayingPath = filePath;
        foreach (var node in EnumerateAll(Roots))
        {
            if (!node.IsFolder)
            {
                node.IsPlaying = !string.IsNullOrEmpty(filePath) &&
                                 string.Equals(node.FilePath, filePath, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    public void Save() => _playlistService.Save(Roots);

    // ───────────────────────── 内部工具 ─────────────────────────

    /// <summary>选中目录 → 该目录；选中视频 → 视频所在目录；未选中 → 最外层(null)</summary>
    private static PlaylistNode? ResolveTargetFolder(PlaylistNode? node)
    {
        if (node == null)
        {
            return null;
        }

        return node.IsFolder ? node : node.Parent;
    }

    private void AddChild(PlaylistNode? parent, PlaylistNode child)
    {
        child.Parent = parent;
        var collection = parent?.Children ?? Roots;

        // 目录排在视频前面，便于浏览
        if (child.IsFolder)
        {
            int insertIndex = collection.TakeWhile(n => n.IsFolder).Count();
            collection.Insert(insertIndex, child);
        }
        else
        {
            collection.Add(child);
        }
    }

    private PlaylistNode? BuildFolderFromDirectory(string directory)
    {
        var folder = new PlaylistNode
        {
            Name = new DirectoryInfo(directory).Name,
            IsFolder = true
        };

        try
        {
            foreach (string subDir in Directory.GetDirectories(directory).OrderBy(d => d, StringComparer.CurrentCultureIgnoreCase))
            {
                var subFolder = BuildFolderFromDirectory(subDir);
                if (subFolder != null)
                {
                    subFolder.Parent = folder;
                    folder.Children.Add(subFolder);
                }
            }

            foreach (string file in Directory.GetFiles(directory).OrderBy(f => f, StringComparer.CurrentCultureIgnoreCase))
            {
                if (VideoExtensions.Contains(Path.GetExtension(file)))
                {
                    folder.Children.Add(new PlaylistNode
                    {
                        Name = Path.GetFileNameWithoutExtension(file),
                        IsFolder = false,
                        FilePath = file,
                        Parent = folder,
                        IsPlaying = string.Equals(file, _currentPlayingPath, StringComparison.OrdinalIgnoreCase)
                    });
                }
            }
        }
        catch
        {
            // 无权限访问的文件夹直接跳过
        }

        return folder.Children.Count > 0 ? folder : null;
    }

    private void Select(PlaylistNode node)
    {
        var parent = node.Parent;
        while (parent != null)
        {
            parent.IsExpanded = true;
            parent = parent.Parent;
        }

        node.IsSelected = true;
        SelectedNode = node;
    }

    private void SaveAndRefresh()
    {
        foreach (var node in EnumerateAll(Roots))
        {
            if (node.IsFolder)
            {
                node.RefreshDisplay();
            }
        }

        Save();
        OnPropertyChanged(nameof(IsEmpty));
    }

    private static IEnumerable<PlaylistNode> EnumerateAll(IEnumerable<PlaylistNode> nodes)
    {
        foreach (var node in nodes)
        {
            yield return node;
            foreach (var child in EnumerateAll(node.Children))
            {
                yield return child;
            }
        }
    }
}
