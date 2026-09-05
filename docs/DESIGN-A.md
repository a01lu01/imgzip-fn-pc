# A 版原生界面规范

唯一原生布局为 A「轻量工具窗」。默认窗口 760 × 640 逻辑像素，按创建时 DPI 转成物理窗口尺寸，采用 Windows 原生标题栏。内容可滚动，底部状态和操作区固定；窗口可调整尺寸。

从上到下为标题与外观/设置、来源、三项预设、执行位置、高级设置、输出位置、底部任务区。启动无示例数据，没有方案切换、模拟状态、多任务队列或历史页面。当前任务进度及失败详情只属于本次任务。

基础字体使用系统 WinUI 字体；标题 24、正文 13–14、说明 11–12，区块标题半粗体。页面水平边距 26，区块间距 12，相关控件间距 8，卡片内边距 12–14；细边框与圆角资源见 `App.xaml`。图标采用系统 FontIcon，不引入位图模拟控件。

`src/ImgZip.App/Themes/Colors.xaml` 保留同名颜色 token，并映射到 XAML Brush 和 WinUI 控件主题资源：

| token | 浅色 | 深色 |
| --- | --- | --- |
| window-bg | #f3f3f3 | #202020 |
| card-bg | #ffffff | #2b2b2b |
| text | #1f1f1f | #ffffff |
| subtext | #5d5d5d | #c7c7c7 |
| hover | #e9e9e9 | #333333 |
| selected | #e3e3e3 | #3a3a3a |
| border | #e0e0e0 | #3c3c3c |
| input-border | #c9c9c9 | #4a4a4a |
| shadow | rgba(0,0,0,.08) | rgba(0,0,0,.35) |
| accent | #6c357c | #7d3d8e |
| accent-text | #6c357c | #a96bb8 |
| success | #107c10 | #6ccb5f |
| warning | #9d5d00 | #fce100 |
| error | #c42b1c | #ff99a4 |
| danger | #c42b1c | #ff99a0 |
| info | #0f6cbd | #6cb8ff |

透明度转换为 XAML 的 8 位 alpha。窗口投影和原生标题栏仍由 Windows 管理，HTML 背景近似不用于验证原生材质。主题默认 `ElementTheme.Default` 跟随系统，手动选择使用 RequestedTheme 并保存配置。

所有入口使用原生 Button、ComboBox、NumberBox、CheckBox、Expander、ContentDialog 等控件；依赖控件原生 Tab/方向键/空格/Enter/Escape 行为。设置和纯图标按钮具有 AutomationProperties.Name。无损禁用质量/目标体积，不缩放禁用尺寸/不放大。执行中锁定来源和任务参数；未知状态只提供重新检查，不能启动另一任务。

视觉、键盘与交互效果尚未验收，由用户在 Windows 对照 A 版完成，检查项见 VALIDATION.md。
