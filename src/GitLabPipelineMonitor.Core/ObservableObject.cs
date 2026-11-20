using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace GitLabPipelineMonitor.Core;

/// <summary>最小的 INotifyPropertyChanged 基类。不引 MVVM 框架：
/// 这个工具只需要属性通知，自己写 20 行比引一个 CommunityToolkit 更符合零依赖。</summary>
public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    /// <summary>值真的变了才通知。返回是否发生了变化。</summary>
    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }

    /// <summary>通知「这一族属性都变了」，用于派生值（比如筛选后的列表）。</summary>
    protected void Refresh(params string[] names)
    {
        foreach (var name in names) OnPropertyChanged(name);
    }
}
