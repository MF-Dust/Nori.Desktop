namespace Nori.Desktop.Settings;

/// <summary>设置字段编辑器；字段属性变化时由 <see cref="SettingsFieldPresenter"/> 转发刷新。</summary>
internal interface ISettingsFieldEditor
{
	/// <summary>按变化的属性名刷新控件。</summary>
	void Refresh(string? propertyName);
}
