using System.Windows;
using LinkFlow.App.ViewModels;
using LinkFlow.Core;

namespace LinkFlow.App.Views;

public sealed class ImportSectionItem : ObservableObject
{
    private bool _isChecked;
    public ImportSectionItem(BackupSections section, string title, string description, bool available)
    {
        Section = section; Title = title; Description = description; IsAvailable = available; _isChecked = available;
    }
    public BackupSections Section { get; }
    public string Title { get; }
    public string Description { get; }
    public bool IsAvailable { get; }
    public bool IsChecked { get => _isChecked; set => SetProperty(ref _isChecked, value); }
}

/// <summary>Escolha do que importar de um backup (.linkflow).</summary>
public partial class ImportDialog : Window
{
    private readonly List<ImportSectionItem> _items;

    public ImportDialog(ConfigBackup backup)
    {
        InitializeComponent();
        FileInfo.Text = $"Backup de {backup.ExportedAt:dd/MM/yyyy HH:mm}" + (backup.Machine is { } m ? $" • {m}" : string.Empty);
        FileDetail.Text = $"LinkFlow {backup.AppVersion ?? "?"} • {backup.Config.Profiles.Count} perfil(is) salvo(s) • {backup.Media.Count} imagem(ns) do LCD";

        ImportSectionItem Item(BackupSections s, string title, bool available = true) => new(s, title, backup.Describe(s), available);
        _items = new List<ImportSectionItem>
        {
            Item(BackupSections.Profiles, "Perfis salvos", backup.Config.Profiles.Count > 0),
            Item(BackupSections.Control, "Modo e curva das ventoinhas"),
            Item(BackupSections.Fans, "Nomes, grupos e ajustes manuais", backup.Config.Fans.Count > 0),
            Item(BackupSections.Lighting, "Iluminação"),
            Item(BackupSections.Lcd, "Tela LCD (quadro, imagens e GIFs)"),
            Item(BackupSections.Safety, "Limites de alerta e modo de segurança"),
            Item(BackupSections.Ui, "Preferências da interface", backup.Ui != null),
        };
        foreach (var i in _items) i.PropertyChanged += (_, _) => UpdateButton();
        SectionList.ItemsSource = _items;
        UpdateButton();
    }

    public BackupSections Sections { get; private set; }

    private void UpdateButton() => ImportButton.IsEnabled = _items.Any(i => i.IsChecked && i.IsAvailable);

    private void All_Click(object sender, RoutedEventArgs e)
    {
        foreach (var i in _items) i.IsChecked = i.IsAvailable;
    }

    private void OnlyProfiles_Click(object sender, RoutedEventArgs e)
    {
        foreach (var i in _items) i.IsChecked = i.IsAvailable && i.Section == BackupSections.Profiles;
    }

    private void Import_Click(object sender, RoutedEventArgs e)
    {
        Sections = _items.Where(i => i.IsChecked && i.IsAvailable).Aggregate(BackupSections.None, (acc, i) => acc | i.Section);
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
