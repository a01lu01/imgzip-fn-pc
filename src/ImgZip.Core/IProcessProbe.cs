namespace ImgZip.Core;

/// <summary>
/// 判断某个进程是否仍存活，并校验启动时刻以排除 PID 复用。
/// </summary>
public interface IProcessProbe
{
    /// <summary>
    /// 进程存在、未退出，且其启动时刻与 <paramref name="startedAt"/> 相符时返回 true。
    /// <paramref name="startedAt"/> 为默认值时无法校验 PID 复用，只能按 PID 判活。
    /// 无法读取（不存在、已退出、访问被拒）时返回 false。
    /// </summary>
    bool IsAlive(int processId, DateTimeOffset startedAt);
}
