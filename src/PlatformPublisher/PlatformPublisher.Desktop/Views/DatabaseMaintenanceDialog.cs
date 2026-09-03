using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using PlatformPublisher.Common.Services;
using PlatformPublisher.Persistence;

namespace PlatformPublisher.Desktop.Views;

public sealed class DatabaseMaintenanceDialog : Window
{
    private readonly PlatformDatabase _database;
    private readonly DatabaseBackupService _backupService;
    private readonly TextBlock _status = new() { TextWrapping = Avalonia.Media.TextWrapping.Wrap };

    public DatabaseMaintenanceDialog(PlatformDatabase database, DatabaseBackupService backupService)
    {
        _database=database;_backupService=backupService;Title="平台数据库";Width=680;Height=330;
        WindowStartupLocation=WindowStartupLocation.CenterOwner;Content=Build();Refresh();
    }

    private Control Build()
    {
        var root=new StackPanel{Margin=new Thickness(18),Spacing=12};
        root.Children.Add(new TextBlock{Text="平台数据库",FontSize=22,FontWeight=Avalonia.Media.FontWeight.Bold});
        root.Children.Add(new TextBlock{Text=PlatformPublisherPaths.MainDatabasePath,TextWrapping=Avalonia.Media.TextWrapping.Wrap,Foreground=Avalonia.Media.Brushes.SlateGray});
        root.Children.Add(_status);
        var actions=new StackPanel{Orientation=Orientation.Horizontal,Spacing=8};
        actions.Children.Add(Button("完整性检查",Refresh));
        actions.Children.Add(Button("创建一致性备份",Backup));
        actions.Children.Add(Button("打开数据库目录",()=>System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe",$"/select,\"{_database.Path}\""){UseShellExecute=true})));
        actions.Children.Add(Button("关闭",Close));root.Children.Add(actions);return root;
    }

    private void Refresh()
    {
        try{var info=new FileInfo(_database.Path);_status.Text=$"状态：{_backupService.IntegrityCheck(_database)}\n大小：{(info.Exists?info.Length/1024d/1024d:0):N2} MB\n备份目录：{PlatformPublisherPaths.BackupRoot}";}
        catch(Exception ex){_status.Text="检查失败："+ex.Message;}
    }

    private void Backup()
    {
        try{var path=_backupService.Backup(_database,PlatformPublisherPaths.BackupRoot);_status.Text="备份完成："+path;}
        catch(Exception ex){_status.Text="备份失败："+ex.Message;}
    }

    private static Button Button(string text,Action action){var button=new Button{Content=text};button.Click+=(_,_)=>action();return button;}
}
