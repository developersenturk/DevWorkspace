using System.IO;
using System.Windows;

namespace DevWorkspace;

public partial class MainWindow
{
    private ReleaseCenterWindow? _releaseCenter;

    private async void OpenProjectReleaseCenter(string projectFile)
    {
        if (_releaseCenter is not null)
        {
            _releaseCenter.Activate();
            await _releaseCenter.SelectProjectAsync(projectFile);
            return;
        }
        _releaseCenter = new ReleaseCenterWindow(projectFile) { Owner = this };
        _releaseCenter.Closed += (_, _) => _releaseCenter = null;
        _releaseCenter.Show();
    }
}
