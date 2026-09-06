using System.Collections.ObjectModel;
using System.Windows.Data;
using ProcOpt.Infrastructure;
using ProcOpt.Models;

namespace ProcOpt.ViewModels;

public class LogViewModel : ObservableObject
{
    private const int MaxEntries = 1000;

    public ObservableCollection<ApplyLogEntry> Entries { get; } = new();

    public LogViewModel()
    {
        View = (ListCollectionView)CollectionViewSource.GetDefaultView(Entries);
    }

    public ListCollectionView View { get; }

    public void Add(ApplyLogEntry entry)
    {
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            Entries.Insert(0, entry);
            while (Entries.Count > MaxEntries)
                Entries.RemoveAt(Entries.Count - 1);
        });
    }

    public RelayCommand ClearCommand => new(() => Entries.Clear());
}
