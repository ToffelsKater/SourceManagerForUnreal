using System.Collections.ObjectModel;
using System.Windows.Input;
using UnrealManager.Core;

namespace UnrealManager.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    public ObservableCollection<PageViewModel> Pages { get; }

    private PageViewModel _currentPage;
    public PageViewModel CurrentPage
    {
        get => _currentPage;
        set => Set(ref _currentPage, value);
    }

    public ICommand SelectCommand { get; }

    public MainViewModel()
    {
        SelectCommand = new RelayCommand(p => { if (p is PageViewModel page) CurrentPage = page; });

        Pages =
        [
            new TutorialViewModel(),
            new DependenciesViewModel(),
            new SourceViewModel(),
            new BuildViewModel(),
            new PerforceViewModel(),
            new SyncLaunchViewModel(),
        ];
        _currentPage = Pages[0];
    }
}








