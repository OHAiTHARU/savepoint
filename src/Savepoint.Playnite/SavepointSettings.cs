using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using Playnite.SDK;
using Playnite.SDK.Data;

namespace Savepoint.Playnite
{
    public abstract class Observable : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;

        protected void Set<T>(ref T field, T value, [CallerMemberName] string name = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return;
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }

    /// <summary>A Playnite game linked to a Savepoint game. Names are cached for display.</summary>
    public class GameLink : Observable
    {
        private string _savepointGameId;
        private string _savepointGameName;

        public Guid PlayniteGameId { get; set; }
        public string PlayniteGameName { get; set; }

        public string SavepointGameId
        {
            get => _savepointGameId;
            set => Set(ref _savepointGameId, value);
        }

        public string SavepointGameName
        {
            get => _savepointGameName;
            set => Set(ref _savepointGameName, value);
        }
    }

    public class SavepointSettings : Observable
    {
        private int _maxWaitSeconds = 45;
        private bool _askWhenUnsynced;
        private bool _notifyOnRestore = true;

        public ObservableCollection<GameLink> Links { get; set; } = new ObservableCollection<GameLink>();

        /// <summary>Longest a launch waits for the agent's pull before starting anyway.</summary>
        public int MaxWaitSeconds
        {
            get => _maxWaitSeconds;
            set => Set(ref _maxWaitSeconds, value);
        }

        /// <summary>Ask before launching if saves couldn't be synced (default: just launch and notify).</summary>
        public bool AskWhenUnsynced
        {
            get => _askWhenUnsynced;
            set => Set(ref _askWhenUnsynced, value);
        }

        public bool NotifyOnRestore
        {
            get => _notifyOnRestore;
            set => Set(ref _notifyOnRestore, value);
        }

        public GameLink LinkFor(Guid playniteGameId) => Links.FirstOrDefault(l => l.PlayniteGameId == playniteGameId);
    }

    /// <summary>Playnite's settings contract: edits happen on <see cref="Settings"/>, and
    /// Cancel restores the copy taken in <see cref="BeginEdit"/>.</summary>
    public class SavepointSettingsViewModel : Observable, ISettings
    {
        private readonly SavepointPlugin _plugin;
        private SavepointSettings _settings;
        private SavepointSettings _backup;

        public SavepointSettingsViewModel(SavepointPlugin plugin)
        {
            _plugin = plugin;
            _settings = plugin.LoadPluginSettings<SavepointSettings>() ?? new SavepointSettings();
        }

        public SavepointSettings Settings
        {
            get => _settings;
            set => Set(ref _settings, value);
        }

        public void Save() => _plugin.SavePluginSettings(Settings);

        public void BeginEdit() => _backup = Serialization.GetClone(Settings);

        public void CancelEdit()
        {
            if (_backup != null) Settings = _backup;
        }

        public void EndEdit() => Save();

        public bool VerifySettings(out List<string> errors)
        {
            errors = new List<string>();
            if (Settings.MaxWaitSeconds < 5 || Settings.MaxWaitSeconds > 600)
                errors.Add("Max wait must be between 5 and 600 seconds.");
            return errors.Count == 0;
        }
    }
}
