using System.Runtime.InteropServices;

namespace ImgZip.App;

/// <summary>
/// 让「点击任务栏图标」把本应用的窗口整体叫到前面。
///
/// 起因：Windows 把同进程的所有窗口合并成一个任务栏按钮（本应用没有设置显式
/// AppUserModelID，AUMID 由 exe 路径派生）。合并后的按钮没有「那一个窗口」可以激活，
/// 于是点一下只会弹出缩略图浮层，所有窗口都留在最小化状态——实测 1 个窗口时正常
/// 还原，2 个以上时一个都回不来。系统没有「还原本组全部窗口」的开关，只能在应用内补偿。
///
/// 做法：给每个窗口的 HWND 挂子类过程，在 <see cref="WmActivateApp"/> 且 wParam != 0
/// （本进程正在成为前台）时判断**接收消息的这个窗口是否就是正在被激活的那个、且它仍处于
/// 最小化**。是，就说明用户正在把一个最小化的窗口叫回来，此时把其余仍最小化的窗口一并还原。
///
/// 为什么两个条件都要，缺一不可：
/// <list type="bullet">
/// <item>只看「自己是最小化」会误伤。WM_ACTIVATEAPP 是按<strong>进程</strong>广播的，
/// 每个顶层窗口都会收到一份；用户故意把 A 最小化、正在用 B 时，A 同样会收到
/// WM_ACTIVATEAPP，此时若只看 IsIconic(A) 就会把 A 也拉起来。</item>
/// <item>再加「前景窗口就是自己」，就把范围收窄到「这一个窗口正在从最小化恢复」——
/// 也就是用户点了任务栏按钮（或其浮层缩略图），再没有别的场景。</item>
/// </list>
///
/// 为什么不用「其余窗口是否都最小化」当闸门：那同样是任何一次激活都可能命中，
/// 后果和第一条一样。
///
/// 附带好处：还原同组窗口后，每个兄弟窗口会再收到一次 WM_ACTIVATEAPP，但那时它已经
/// 不是最小化了，闸门不成立，天然不会递归——一轮就结束。这里仍留一个 <see cref="revealing"/>
/// 标志做兜底，代价是一次布尔判断。
/// </summary>
internal static class WindowReveal
{
    private const uint WmActivateApp = 0x001C;
    private const uint SubclassId = 0x494D_475A; // 'IMGZ'，同一 HWND 只需唯一

    private static Action<IntPtr>? onReveal;
    private static bool revealing;

    /// <summary>挂上子类过程。<paramref name="reveal"/> 收到的是「正在从最小化恢复」的窗口句柄。</summary>
    public static void Attach(IntPtr handle, Action<IntPtr> reveal)
    {
        if (handle == IntPtr.Zero) return;
        onReveal = reveal;
        SetWindowSubclass(handle, Proc, SubclassId, IntPtr.Zero);
    }

    public static void Detach(IntPtr handle)
    {
        if (handle == IntPtr.Zero) return;
        RemoveWindowSubclass(handle, Proc, SubclassId);
    }

    private static IntPtr Proc(IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam, IntPtr id, IntPtr data)
    {
        // 只在我们自己的窗口刚从最小化恢复时动手；别的激活一律放行。
        if (message == WmActivateApp && wParam != IntPtr.Zero && !revealing
            && GetForegroundWindow() == hWnd && IsIconic(hWnd))
        {
            revealing = true;
            try { onReveal?.Invoke(hWnd); }
            catch { }
            finally { revealing = false; }
        }
        return DefSubclassProc(hWnd, message, wParam, lParam);
    }

    private delegate IntPtr SubclassProc(IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam, IntPtr id, IntPtr data);

    [DllImport("comctl32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowSubclass(IntPtr hWnd, SubclassProc procedure, uint id, IntPtr data);
    [DllImport("comctl32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveWindowSubclass(IntPtr hWnd, SubclassProc procedure, uint id);
    [DllImport("comctl32.dll")]
    private static extern IntPtr DefSubclassProc(IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr hWnd);
}
