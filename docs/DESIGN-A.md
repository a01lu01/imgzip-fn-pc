# A 版原生界面规范

唯一原生布局为 A「轻量工具窗」。0.3.4 保留 0.3.3 已调整的默认 860 × 740 逻辑像素，加载后按窗口实际 DPI 和显示器工作区计算，记忆位置与尺寸。采用 XAML 自绘标题栏，保留系统窗口按钮；内容可滚动，底部任务区固定，窗口可调整尺寸。最初 HTML 的 760 × 640 仅为早期选型尺寸。

从上到下为标题与外观/设置、来源、三项预设、执行位置、高级设置、输出位置、底部任务区。启动无示例数据，没有方案切换、模拟状态或历史页面。保留 0.3.3 多任务列表，进度及失败详情按任务隔离；成功任务自动移出列表。

基础字体使用系统 WinUI 字体；标题 24、正文 13–14、说明 11–12，区块标题半粗体。页面水平边距 26，区块间距 12，相关控件间距 8，卡片内边距 12–14；细边框与圆角资源见 `App.xaml`。功能图标采用系统 FontIcon；应用图标使用 `icon/` 中的 ICO 与 PNG。

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

透明度转换为 XAML 的 8 位 alpha。窗口投影由 Windows 管理，标题栏内容及系统按钮前景随主题更新；HTML 背景近似不用于验证原生材质。主题默认 `ElementTheme.Default` 跟随系统，手动选择使用 RequestedTheme 并保存配置。

所有入口使用原生 Button、ToggleButton、ComboBox、NumberBox、CheckBox、Expander、ContentDialog 等控件；依赖控件原生 Tab/方向键/空格/Enter/Escape 行为。设置和纯图标按钮具有 AutomationProperties.Name。无损禁用质量/目标体积，不缩放禁用尺寸/不放大。草稿区在初始化后保持可编辑；提交任务采用参数快照，不随草稿变化。

排队任务提供“移除”，重启恢复的未启动任务提供“继续”和“移除”，执行中的任务提供“取消”，未知任务提供“重新检查”。未知任务继续占用名额，其他任务按剩余 PC/NAS 名额调度。失败详情、任务错误及可打开的输出路径随事件刷新。关闭窗口判断全部未结束任务，“取消并等待”不能把未确认任务显示为已停止。

0.3.3 的既有用户实测记录见 `UI-ISSUES-0.3.0-0.3.3.md`；0.3.4 的视觉、键盘与交互验收由用户在 Windows 完成，检查项见 VALIDATION.md。
