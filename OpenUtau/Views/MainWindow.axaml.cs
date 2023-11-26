﻿using System;
using System.ComponentModel;
using System.Linq;
using System.Reactive;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using OpenUtau.App.Controls;
using OpenUtau.App.ViewModels;
using OpenUtau.Classic;
using OpenUtau.Core;
using OpenUtau.Core.Format;
using OpenUtau.Core.Ustx;
using OpenUtau.Core.Util;
using ReactiveUI;
using Serilog;
using Point = Avalonia.Point;

using OpenUtau.Core.DiffSinger;
using Format;
using System.Collections.Generic;

namespace OpenUtau.App.Views {
    public partial class MainWindow : Window, ICmdSubscriber {
        private readonly KeyModifiers cmdKey =
            OS.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control;
        private readonly MainWindowViewModel viewModel;

        private bool splashDone = false;

        private PianoRollWindow? pianoRollWindow;
        private bool openPianoRollWindow;

        private PartEditState? partEditState;
        private readonly DispatcherTimer timer;
        private readonly DispatcherTimer autosaveTimer;
        private bool forceClose;

        private bool shouldOpenPartsContextMenu;

        private readonly ReactiveCommand<UPart, Unit> PartRenameCommand;
        private readonly ReactiveCommand<UPart, Unit> PartReplaceAudioCommand;

        public MainWindow() {
            Log.Information("Creating main window.");
            InitializeComponent();
            Log.Information("Initialized main window component.");
            DataContext = viewModel = new MainWindowViewModel();
            var scheduler = TaskScheduler.FromCurrentSynchronizationContext();
            viewModel.GetInitSingerTask()!.ContinueWith(_ => {
                viewModel.InitProject();
                viewModel.AddTempoChangeCmd = ReactiveCommand.Create<int>(tick => AddTempoChange(tick));
                viewModel.DelTempoChangeCmd = ReactiveCommand.Create<int>(tick => DelTempoChange(tick));
                viewModel.AddTimeSigChangeCmd = ReactiveCommand.Create<int>(bar => AddTimeSigChange(bar));
                viewModel.DelTimeSigChangeCmd = ReactiveCommand.Create<int>(bar => DelTimeSigChange(bar));

                Splash.IsEnabled = false;
                Splash.IsVisible = false;
                MainGrid.IsEnabled = true;
                MainGrid.IsVisible = true;
                splashDone = true;
            }, CancellationToken.None, TaskContinuationOptions.None, scheduler);

            timer = new DispatcherTimer(
                TimeSpan.FromMilliseconds(15),
                DispatcherPriority.Normal,
                (sender, args) => PlaybackManager.Inst.UpdatePlayPos());
            timer.Start();

            autosaveTimer = new DispatcherTimer(
                TimeSpan.FromSeconds(30),
                DispatcherPriority.Normal,
                (sender, args) => DocManager.Inst.AutoSave());
            autosaveTimer.Start();

            PartRenameCommand = ReactiveCommand.Create<UPart>(part => RenamePart(part));
            PartReplaceAudioCommand = ReactiveCommand.Create<UPart>(part => ReplaceAudio(part));

            AddHandler(DragDrop.DropEvent, OnDrop);

            DocManager.Inst.AddSubscriber(this);

            Log.Information("Main window checking Update.");
            UpdaterDialog.CheckForUpdate(
                dialog => dialog.Show(this),
                () => (Application.Current?.ApplicationLifetime as IControlledApplicationLifetime)?.Shutdown(),
                TaskScheduler.FromCurrentSynchronizationContext());
            Log.Information("Created main window.");
        }

        void OnEditTimeSignature(object sender, PointerPressedEventArgs args) {
            var project = DocManager.Inst.Project;
            var timeSig = project.timeSignatures[0];
            var dialog = new TimeSignatureDialog(timeSig.beatPerBar, timeSig.beatUnit);
            dialog.OnOk = (beatPerBar, beatUnit) => {
                viewModel.PlaybackViewModel.SetTimeSignature(beatPerBar, beatUnit);
            };
            dialog.ShowDialog(this);
            // Workaround for https://github.com/AvaloniaUI/Avalonia/issues/3986
            args.Pointer.Capture(null);
        }

        void OnEditBpm(object sender, PointerPressedEventArgs args) {
            var project = DocManager.Inst.Project;
            var dialog = new TypeInDialog();
            dialog.Title = "BPM";
            dialog.SetText(project.tempos[0].bpm.ToString());
            dialog.onFinish = s => {
                if (double.TryParse(s, out double bpm)) {
                    viewModel.PlaybackViewModel.SetBpm(bpm);
                }
            };
            dialog.ShowDialog(this);
            // Workaround for https://github.com/AvaloniaUI/Avalonia/issues/3986
            args.Pointer.Capture(null);
        }

        private void AddTempoChange(int tick) {
            var project = DocManager.Inst.Project;
            var dialog = new TypeInDialog {
                Title = "BPM"
            };
            dialog.SetText(project.tempos[0].bpm.ToString());
            dialog.onFinish = s => {
                if (double.TryParse(s, out double bpm)) {
                    DocManager.Inst.StartUndoGroup();
                    DocManager.Inst.ExecuteCmd(new AddTempoChangeCommand(
                        project, tick, bpm));
                    DocManager.Inst.EndUndoGroup();
                }
            };
            dialog.ShowDialog(this);
        }

        private void DelTempoChange(int tick) {
            var project = DocManager.Inst.Project;
            DocManager.Inst.StartUndoGroup();
            DocManager.Inst.ExecuteCmd(new DelTempoChangeCommand(project, tick));
            DocManager.Inst.EndUndoGroup();
        }

        private void AddTimeSigChange(int bar) {
            var project = DocManager.Inst.Project;
            var timeSig = project.timeAxis.TimeSignatureAtBar(bar);
            var dialog = new TimeSignatureDialog(timeSig.beatPerBar, timeSig.beatUnit);
            dialog.OnOk = (beatPerBar, beatUnit) => {
                DocManager.Inst.StartUndoGroup();
                DocManager.Inst.ExecuteCmd(new AddTimeSigCommand(
                    project, bar, dialog.BeatPerBar, dialog.BeatUnit));
                DocManager.Inst.EndUndoGroup();
            };
            dialog.ShowDialog(this);
        }

        private void DelTimeSigChange(int bar) {
            var project = DocManager.Inst.Project;
            DocManager.Inst.StartUndoGroup();
            DocManager.Inst.ExecuteCmd(new DelTimeSigCommand(project, bar));
            DocManager.Inst.EndUndoGroup();
        }

        void OnMenuNew(object sender, RoutedEventArgs args) => NewProject();
        async void NewProject() {
            if (!DocManager.Inst.ChangesSaved && !await AskIfSaveAndContinue()) {
                return;
            }
            viewModel.NewProject();
        }

        void OnMenuOpen(object sender, RoutedEventArgs args) => Open();
        async void Open() {
            if (!DocManager.Inst.ChangesSaved && !await AskIfSaveAndContinue()) {
                return;
            }
            var files = await FilePicker.OpenFiles(
                this, "menu.file.open",
                FilePicker.ProjectFiles,
                FilePicker.USTX,
                FilePicker.VSQX,
                FilePicker.UST,
                FilePicker.MIDI,
                FilePicker.UFDATA);
            if (files == null || files.Length == 0) {
                return;
            }
            try {
                viewModel.OpenProject(files);
            } catch (Exception e) {
                Log.Error(e, $"Failed to open files {string.Join("\n", files)}");
                _ = await MessageBox.ShowError(this, e);
            }
        }

        void OnMainMenuOpened(object sender, RoutedEventArgs args) {
            viewModel.RefreshOpenRecent();
            viewModel.RefreshTemplates();
            viewModel.RefreshCacheSize();
        }

        void OnMainMenuClosed(object sender, RoutedEventArgs args) {
            Focus(); // Force unfocus menu for key down events.
        }

        void OnMainMenuPointerLeave(object sender, PointerEventArgs args) {
            Focus(); // Force unfocus menu for key down events.
        }

        void OnMenuOpenProjectLocation(object sender, RoutedEventArgs args) {
            var project = DocManager.Inst.Project;
            if (string.IsNullOrEmpty(project.FilePath) || !project.Saved) {
                MessageBox.Show(
                    this,
                    ThemeManager.GetString("dialogs.export.savefirst"),
                    ThemeManager.GetString("errors.caption"),
                    MessageBox.MessageBoxButtons.Ok);
            }
            try {
                var dir = System.IO.Path.GetDirectoryName(project.FilePath);
                if (dir != null) {
                    OS.OpenFolder(dir);
                } else {
                    Log.Error($"Failed to get project location from {dir}.");
                }
            } catch (Exception e) {
                Log.Error(e, "Failed to open project location.");
                MessageBox.ShowError(this, e);
            }
        }

        async void OnMenuSave(object sender, RoutedEventArgs args) => await Save();
        public async Task Save() {
            if (!viewModel.ProjectSaved) {
                await SaveAs();
            } else {
                viewModel.SaveProject();
                string message = ThemeManager.GetString("progress.saved");
                message = string.Format(message, DateTime.Now);
                DocManager.Inst.ExecuteCmd(new ProgressBarNotification(0, message));
            }
        }

        async void OnMenuSaveAs(object sender, RoutedEventArgs args) => await SaveAs();
        async Task SaveAs() {
            var file = await FilePicker.SaveFile(
                this, "menu.file.saveas", FilePicker.USTX);
            if (!string.IsNullOrEmpty(file)) {
                viewModel.SaveProject(file);
            }
        }

        void OnMenuSaveTemplate(object sender, RoutedEventArgs args) {
            var project = DocManager.Inst.Project;
            var dialog = new TypeInDialog();
            dialog.Title = ThemeManager.GetString("menu.file.savetemplate");
            dialog.SetText("default");
            dialog.onFinish = file => {
                if (string.IsNullOrEmpty(file)) {
                    return;
                }
                file = System.IO.Path.GetFileNameWithoutExtension(file);
                file = $"{file}.ustx";
                file = System.IO.Path.Combine(PathManager.Inst.TemplatesPath, file);
                Ustx.Save(file, project.CloneAsTemplate());
            };
            dialog.ShowDialog(this);
        }

        async void OnMenuImportTracks(object sender, RoutedEventArgs args) {
            var files = await FilePicker.OpenFiles(
                this, "menu.file.importtracks",
                FilePicker.ProjectFiles,
                FilePicker.USTX,
                FilePicker.VSQX,
                FilePicker.UST,
                FilePicker.MIDI);
            if (files == null || files.Length == 0) {
                return;
            }
            try {
                viewModel.ImportTracks(files);
            } catch (Exception e) {
                Log.Error(e, $"Failed to import files");
                _ = await MessageBox.ShowError(this, e);
            }
            ValidateTracksVoiceColor();
        }

        async void OnMenuImportAudio(object sender, RoutedEventArgs args) {
            var file = await FilePicker.OpenFile(
                this, "menu.file.importaudio", FilePicker.AudioFiles);
            if (file == null) {
                return;
            }
            try {
                viewModel.ImportAudio(file);
            } catch (Exception e) {
                Log.Error(e, "Failed to import audio");
                _ = await MessageBox.ShowError(this, e);
            }
        }

        async void OnMenuImportMidi(bool UseDrywetmidi = false) {
            var file = await FilePicker.OpenFile(
                this, "menu.file.importmidi", FilePicker.MIDI);
            if (file == null) {
                return;
            }
            try {
                viewModel.ImportMidi(file, UseDrywetmidi);
            } catch (Exception e) {
                Log.Error(e, "Failed to import midi");
                _ = await MessageBox.ShowError(this, e);
            }
        }

        void OnMenuImportMidiNaudio(object sender, RoutedEventArgs args) {
            OnMenuImportMidi(false);
        }

        void OnMenuImportMidiDrywetmidi(object sender, RoutedEventArgs args) {
            OnMenuImportMidi(true);
        }

        async void OnMenuExportMixdown(object sender, RoutedEventArgs args) {
            var project = DocManager.Inst.Project;
            var file = await FilePicker.SaveFile(
                this, "menu.file.exportmixdown", FilePicker.WAV);
            if (!string.IsNullOrEmpty(file)) {
                PlaybackManager.Inst.RenderMixdown(project, file);
            }
        }

        async void OnMenuExportWav(object sender, RoutedEventArgs args) {
            var project = DocManager.Inst.Project;
            if (await WarnToSave(project)) {
                var name = System.IO.Path.GetFileNameWithoutExtension(project.FilePath);
                var path = System.IO.Path.GetDirectoryName(project.FilePath);
                path = System.IO.Path.Combine(path!, "Export", $"{name}.wav");
                PlaybackManager.Inst.RenderToFiles(project, path);
            }
        }

        async void OnMenuExportWavTo(object sender, RoutedEventArgs args) {
            var project = DocManager.Inst.Project;
            var file = await FilePicker.SaveFile(
                this, "menu.file.exportwavto", FilePicker.WAV);
            if (!string.IsNullOrEmpty(file)) {
                PlaybackManager.Inst.RenderToFiles(project, file);
            }
        }

        async void OnMenuExportDsTo(object sender, RoutedEventArgs e) {
            var project = DocManager.Inst.Project;
            var file = await FilePicker.SaveFile(
                this, "menu.file.exportds", FilePicker.DS);
            if (!string.IsNullOrEmpty(file)) {
                for (var i = 0; i < project.parts.Count; i++) {
                    var part = project.parts[i];
                    if (part is UVoicePart voicePart) {
                        var savePath =  PathManager.Inst.GetPartSavePath(file, voicePart.DisplayName, i)[..^4]+".ds";
                        DiffSingerScript.SavePart(project, voicePart, savePath);
                        DocManager.Inst.ExecuteCmd(new ProgressBarNotification(0, $"{savePath}."));
                    }
                }
            }
        }

        async void OnMenuExportDsV2To(object sender, RoutedEventArgs e) {
            var project = DocManager.Inst.Project;
            var file = await FilePicker.SaveFile(
                this, "menu.file.exportds.v2", FilePicker.DS);
            if (!string.IsNullOrEmpty(file)) {
                for (var i = 0; i < project.parts.Count; i++) {
                    var part = project.parts[i];
                    if (part is UVoicePart voicePart) {
                        var savePath =  PathManager.Inst.GetPartSavePath(file, voicePart.DisplayName, i)[..^4]+".ds";
                        DiffSingerScript.SavePart(project, voicePart, savePath, true);
                        DocManager.Inst.ExecuteCmd(new ProgressBarNotification(0, $"{savePath}."));
                    }
                }
            }
        }

        async void OnMenuExportDsV2WithoutPitchTo(object sender, RoutedEventArgs e) {
            var project = DocManager.Inst.Project;
            var file = await FilePicker.SaveFile(
                this, "menu.file.exportds.v2withoutpitch", FilePicker.DS);
            if (!string.IsNullOrEmpty(file)) {
                for (var i = 0; i < project.parts.Count; i++) {
                    var part = project.parts[i];
                    if (part is UVoicePart voicePart) {
                        var savePath =  PathManager.Inst.GetPartSavePath(file, voicePart.DisplayName, i)[..^4]+".ds";
                        DiffSingerScript.SavePart(project, voicePart, savePath, true, false);
                        DocManager.Inst.ExecuteCmd(new ProgressBarNotification(0, $"{savePath}."));
                    }
                }
            }
        }

        async void OnMenuExportLabTo(object sender, RoutedEventArgs e) {
            var project = DocManager.Inst.Project;
            var file = await FilePicker.SaveFile(
                this, "menu.file.exportlab", FilePicker.LAB);
            if (!string.IsNullOrEmpty(file)) {
                for (var i = 0; i < project.parts.Count; i++) {
                    var part = project.parts[i];
                    if (part is UVoicePart voicePart) {
                        var savePath = PathManager.Inst.GetPartSavePath(file, voicePart.DisplayName, i)[..^4] + ".lab";
                        Lab.SavePart(project, voicePart, savePath);
                        DocManager.Inst.ExecuteCmd(new ProgressBarNotification(0, $"{savePath}."));
                    }
                }
            }
        }

        async void OnMenuExportLabAndWavTo(object sender, RoutedEventArgs e) {
            var project = DocManager.Inst.Project;
            var file = await FilePicker.SaveFile(
                this, "menu.file.exportlabandwav", FilePicker.WAV);
            if (!string.IsNullOrEmpty(file)) {
                List<string> paths = new List<string>();
                for (var i = 0; i < project.parts.Count; i++) {
                    var part = project.parts[i];
                    if (part is UVoicePart voicePart) {
                        paths.Add(PathManager.Inst.GetExportPath(file, project.tracks[i])[..^4]);
                        Lab.SavePart(project, voicePart, paths[i] + ".lab");
                        DocManager.Inst.ExecuteCmd(new ProgressBarNotification(0, $"{paths[i] + ".lab"}."));
                    }
                }
                PlaybackManager.Inst.RenderToFiles(project, file + ".wav");
            }
        }

        async void OnMenuExportUst(object sender, RoutedEventArgs e) {
            var project = DocManager.Inst.Project;
            if (await WarnToSave(project)) {
                var name = System.IO.Path.GetFileNameWithoutExtension(project.FilePath);
                var path = System.IO.Path.GetDirectoryName(project.FilePath);
                path = System.IO.Path.Combine(path!, "Export", $"{name}.ust");
                for (var i = 0; i < project.parts.Count; i++) {
                    var part = project.parts[i];
                    if (part is UVoicePart voicePart) {
                        var savePath = PathManager.Inst.GetPartSavePath(path, voicePart.DisplayName, i);
                        Ust.SavePart(project, voicePart, savePath);
                        DocManager.Inst.ExecuteCmd(new ProgressBarNotification(0, $"{savePath}."));
                    }
                }
            }
        }

        async void OnMenuExportUstTo(object sender, RoutedEventArgs e) {
            var project = DocManager.Inst.Project;
            var file = await FilePicker.SaveFile(
                this, "menu.file.exportustto", FilePicker.UST);
            if (!string.IsNullOrEmpty(file)) {
                for (var i = 0; i < project.parts.Count; i++) {
                    var part = project.parts[i];
                    if (part is UVoicePart voicePart) {
                        var savePath = PathManager.Inst.GetPartSavePath(file, voicePart.DisplayName, i);
                        Ust.SavePart(project, voicePart, savePath);
                        DocManager.Inst.ExecuteCmd(new ProgressBarNotification(0, $"{savePath}."));
                    }
                }
            }
        }

        async void OnMenuExportMidi(object sender, RoutedEventArgs e) {
            var project = DocManager.Inst.Project;
            var file = await FilePicker.SaveFile(
                this, "menu.file.exportmidi", FilePicker.MIDI);
            if (!string.IsNullOrEmpty(file)) {
                MidiWriter.Save(file, project);
            }
        }

        private async Task<bool> WarnToSave(UProject project) {
            if (string.IsNullOrEmpty(project.FilePath)) {
                await MessageBox.Show(
                    this,
                    ThemeManager.GetString("dialogs.export.savefirst"),
                    ThemeManager.GetString("dialogs.export.caption"),
                    MessageBox.MessageBoxButtons.Ok);
                return false;
            }
            return true;
        }

        void OnMenuExpressionss(object sender, RoutedEventArgs args) {
            var dialog = new ExpressionsDialog() {
                DataContext = new ExpressionsViewModel(),
            };
            dialog.ShowDialog(this);
            if (dialog.Position.Y < 0) {
                dialog.Position = dialog.Position.WithY(0);
            }
        }

        void OnMenuSingers(object sender, RoutedEventArgs args) {
            OpenSingersWindow();
        }

        public void OpenSingersWindow() {
            var lifetime = Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime;
            if (lifetime == null) {
                return;
            }
            var dialog = lifetime.Windows.FirstOrDefault(w => w is SingersDialog);
            if (dialog == null) {
                USinger? singer = null;
                if (viewModel.TracksViewModel.SelectedParts.Count > 0) {
                    singer = viewModel.TracksViewModel.Tracks[viewModel.TracksViewModel.SelectedParts.First().trackNo].Singer;
                }
                if (singer == null && viewModel.TracksViewModel.Tracks.Count > 0) {
                    singer = viewModel.TracksViewModel.Tracks.First().Singer;
                }
                var vm = new SingersViewModel();
                if (singer != null) {
                    vm.Singer = singer;
                }
                dialog = new SingersDialog() { DataContext = vm };
                dialog.Show();
            }
            dialog.Activate();
            if (dialog.Position.Y < 0) {
                dialog.Position = dialog.Position.WithY(0);
            }
        }

        async void OnMenuInstallSinger(object sender, RoutedEventArgs args) {
            var file = await FilePicker.OpenFile(
                this, "menu.tools.singer.install", FilePicker.ArchiveFiles);
            if (file == null) {
                return;
            }
            if (file.EndsWith(Core.Vogen.VogenSingerInstaller.FileExt)) {
                Core.Vogen.VogenSingerInstaller.Install(file);
                return;
            }
            if (file.EndsWith(DependencyInstaller.FileExt)) {
                DependencyInstaller.Install(file);
                return;
            }
            try {
                var setup = new SingerSetupDialog() {
                    DataContext = new SingerSetupViewModel() {
                        ArchiveFilePath = file,
                    },
                };
                _ = setup.ShowDialog(this);
                if (setup.Position.Y < 0) {
                    setup.Position = setup.Position.WithY(0);
                }
            } catch (Exception e) {
                _ = MessageBox.ShowError(this, e);
            }
        }

        void OnMenuPreferences(object sender, RoutedEventArgs args) {
            var dialog = new PreferencesDialog() {
                DataContext = new PreferencesViewModel(),
            };
            dialog.ShowDialog(this);
            if (dialog.Position.Y < 0) {
                dialog.Position = dialog.Position.WithY(0);
            }
        }

        void OnMenuClearCache(object sender, RoutedEventArgs args) {
            Task.Run(() => {
                DocManager.Inst.ExecuteCmd(new ProgressBarNotification(0, "Clearing cache..."));
                PathManager.Inst.ClearCache();
                DocManager.Inst.ExecuteCmd(new ProgressBarNotification(0, "Cache cleared."));
            });
        }

        void OnMenuDebugWindow(object sender, RoutedEventArgs args) {
            var desktop = Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime;
            if (desktop == null) {
                return;
            }
            var window = desktop.Windows.FirstOrDefault(w => w is DebugWindow);
            if (window == null) {
                window = new DebugWindow();
            }
            window.Show();
        }

        void OnMenuPhoneticAssistant(object sender, RoutedEventArgs args) {
            var desktop = Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime;
            if (desktop == null) {
                return;
            }
            var window = desktop.Windows.FirstOrDefault(w => w is PhoneticAssistant);
            if (window == null) {
                window = new PhoneticAssistant();
            }
            window.Show();
        }

        void OnMenuCheckUpdate(object sender, RoutedEventArgs args) {
            var dialog = new UpdaterDialog();
            dialog.ViewModel.CloseApplication =
                () => (Application.Current?.ApplicationLifetime as IControlledApplicationLifetime)?.Shutdown();
            dialog.ShowDialog(this);
        }

        void OnMenuLogsLocation(object sender, RoutedEventArgs args) {
            try {
                OS.OpenFolder(PathManager.Inst.LogsPath);
            } catch (Exception e) {
                DocManager.Inst.ExecuteCmd(new ErrorMessageNotification(e));
            }
        }

        void OnMenuReportIssue(object sender, RoutedEventArgs args) {
            try {
                OS.OpenWeb("https://github.com/stakira/OpenUtau/issues");
            } catch (Exception e) {
                DocManager.Inst.ExecuteCmd(new ErrorMessageNotification(e));
            }
        }

        void OnMenuWiki(object sender, RoutedEventArgs args) {
            try {
                OS.OpenWeb("https://github.com/stakira/OpenUtau/wiki/Getting-Started");
            } catch (Exception e) {
                DocManager.Inst.ExecuteCmd(new ErrorMessageNotification(e));
            }
        }

        void OnMenuLayoutVSplit11(object sender, RoutedEventArgs args) => LayoutSplit(null, 1.0 / 2);
        void OnMenuLayoutVSplit12(object sender, RoutedEventArgs args) => LayoutSplit(null, 1.0 / 3);
        void OnMenuLayoutVSplit13(object sender, RoutedEventArgs args) => LayoutSplit(null, 1.0 / 4);
        void OnMenuLayoutHSplit11(object sender, RoutedEventArgs args) => LayoutSplit(1.0 / 2, null);
        void OnMenuLayoutHSplit12(object sender, RoutedEventArgs args) => LayoutSplit(1.0 / 3, null);
        void OnMenuLayoutHSplit13(object sender, RoutedEventArgs args) => LayoutSplit(1.0 / 4, null);

        private void LayoutSplit(double? x, double? y) {
            if (Screens.Primary == null) {
                return;
            }
            var wa = Screens.Primary.WorkingArea;
            WindowState = WindowState.Normal;
            double titleBarHeight = 20;
            if (FrameSize != null) {
                double borderThickness = (FrameSize!.Value.Width - ClientSize.Width) / 2;
                titleBarHeight = FrameSize!.Value.Height - ClientSize.Height - borderThickness;
            }
            Position = new PixelPoint(0, 0);
            Width = x != null ? wa.Size.Width * x.Value : wa.Size.Width;
            Height = (y != null ? wa.Size.Height * y.Value : wa.Size.Height) - titleBarHeight;
            if (pianoRollWindow != null) {
                pianoRollWindow.Position = new PixelPoint(x != null ? (int) Width : 0, y != null ? (int) (Height + (OS.IsMacOS() ? 25 : titleBarHeight)) : 0);
                pianoRollWindow.Width = x != null ? wa.Size.Width - Width : wa.Size.Width;
                pianoRollWindow.Height = (y != null ? wa.Size.Height - (Height + titleBarHeight) : wa.Size.Height) - titleBarHeight;
            }
        }

        void OnKeyDown(object sender, KeyEventArgs args) {
            if (!splashDone) {
                return;
            }
            var tracksVm = viewModel.TracksViewModel;
            if (args.KeyModifiers == KeyModifiers.None){
                args.Handled = true;
                switch (args.Key) {
                    case Key.Delete: viewModel.TracksViewModel.DeleteSelectedParts(); break;
                    case Key.Space: PlayOrPause(); break;
                    case Key.Home: viewModel.PlaybackViewModel.MovePlayPos(0); break;
                    case Key.End:
                        if (viewModel.TracksViewModel.Parts.Count > 0) {
                            int endTick = viewModel.TracksViewModel.Parts.Max(part => part.End);
                            viewModel.PlaybackViewModel.MovePlayPos(endTick);
                        }
                        break;
                    default:
                        args.Handled = false;
                        break;
                }
            } else if (args.KeyModifiers == KeyModifiers.Alt) {
                args.Handled = true;
                switch (args.Key) {
                    case Key.F4:
                        (Application.Current?.ApplicationLifetime as IControlledApplicationLifetime)?.Shutdown();
                        break;
                    default:
                        args.Handled = false;
                        break;
                }
            } else if (args.KeyModifiers == cmdKey) {
                args.Handled = true;
                switch (args.Key) {
                    case Key.A: viewModel.TracksViewModel.SelectAllParts(); break;
                    case Key.N: NewProject(); break;
                    case Key.O: Open(); break;
                    case Key.S: _ = Save(); break;
                    case Key.Z: viewModel.Undo(); break;
                    case Key.Y: viewModel.Redo(); break;
                    case Key.C: tracksVm.CopyParts(); break;
                    case Key.X: tracksVm.CutParts(); break;
                    case Key.V: tracksVm.PasteParts(); break;
                    default:
                        args.Handled = false;
                        break;
                }
            } else if (args.KeyModifiers == KeyModifiers.Shift) {
                args.Handled = true;
                switch (args.Key) {
                    // solo
                    case Key.S:
                        if (viewModel.TracksViewModel.SelectedParts.Count > 0) {
                            var part = viewModel.TracksViewModel.SelectedParts.First();
                            var track = DocManager.Inst.Project.tracks[part.trackNo];
                            MessageBus.Current.SendMessage(new TracksSoloEvent(part.trackNo, !track.Solo, false));
                        }
                        break;
                    // mute
                    case Key.M:
                        if (viewModel.TracksViewModel.SelectedParts.Count > 0) {
                            var part = viewModel.TracksViewModel.SelectedParts.First();
                            MessageBus.Current.SendMessage(new TracksMuteEvent(part.trackNo, false));
                        }
                        break;
                    default:
                        args.Handled = false;
                        break;
                }
            } else if (args.KeyModifiers == (cmdKey | KeyModifiers.Shift)) {
                args.Handled = true;
                switch (args.Key) {
                    case Key.Z: viewModel.Redo(); break;
                    case Key.S: _ = SaveAs(); break;
                    default:
                        args.Handled = false;
                        break;
                }
            }
        }

        void OnPointerPressed(object? sender, PointerPressedEventArgs args) {
            if (!args.Handled && args.ClickCount == 1) {
                FocusManager?.ClearFocus();
            }
        }

        async void OnDrop(object? sender, DragEventArgs args) {
            var storageItem = args.Data?.GetFiles()?.FirstOrDefault();
            if (storageItem == null) {
                return;
            }
            string file = storageItem.Path.LocalPath;
            var ext = System.IO.Path.GetExtension(file);
            if (ext == ".ustx" || ext == ".ust" || ext == ".vsqx" || ext==".ufdata") {
                if (!DocManager.Inst.ChangesSaved && !await AskIfSaveAndContinue()) {
                    return;
                }
                try {
                    viewModel.OpenProject(new string[] { file });
                } catch (Exception e) {
                    Log.Error(e, $"Failed to open file {file}");
                    _ = await MessageBox.ShowError(this, e);
                }
            } else if (ext == ".mid") {
                try {
                    viewModel.ImportMidi(file);
                } catch (Exception e) {
                    Log.Error(e, "Failed to import midi");
                    _ = await MessageBox.ShowError(this, e);
                }
            } else if (ext == ".zip" || ext == ".rar" || ext == ".uar") {
                var setup = new SingerSetupDialog() {
                    DataContext = new SingerSetupViewModel() {
                        ArchiveFilePath = file,
                    },
                };
                _ = setup.ShowDialog(this);
                if (setup.Position.Y < 0) {
                    setup.Position = setup.Position.WithY(0);
                }
            } else if (ext == Core.Vogen.VogenSingerInstaller.FileExt) {
                Core.Vogen.VogenSingerInstaller.Install(file);
            } else if (ext == ".dll") {
                var result = await MessageBox.Show(
                    this,
                    ThemeManager.GetString("dialogs.installdll.message") + file,
                    ThemeManager.GetString("dialogs.installdll.caption"),
                    MessageBox.MessageBoxButtons.OkCancel);
                if (result == MessageBox.MessageBoxResult.Ok) {
                    Core.Api.PhonemizerInstaller.Install(file);
                }
            } else if (ext == ".exe") {
                var setup = new ExeSetupDialog() {
                    DataContext = new ExeSetupViewModel(file)
                };
                _ = setup.ShowDialog(this);
                if (setup.Position.Y < 0) {
                    setup.Position = setup.Position.WithY(0);
                }
            } else if (ext == DependencyInstaller.FileExt) {
                var result = await MessageBox.Show(
                    this,
                    ThemeManager.GetString("dialogs.installdependency.message") + file,
                    ThemeManager.GetString("dialogs.installdependency.caption"),
                    MessageBox.MessageBoxButtons.OkCancel);
                if (result == MessageBox.MessageBoxResult.Ok) {
                    DependencyInstaller.Install(file);
                }
            } else if (ext == ".mp3" || ext == ".wav" || ext == ".ogg" || ext == ".flac") {
                try {
                    viewModel.ImportAudio(file);
                } catch (Exception e) {
                    Log.Error(e, "Failed to import audio");
                    _ = await MessageBox.ShowError(this, e);
                }
            }
        }

        void OnPlayOrPause(object sender, RoutedEventArgs args) {
            PlayOrPause();
        }

        void PlayOrPause() {
            try {
                viewModel.PlaybackViewModel.PlayOrPause();
            } catch (Core.Render.NoResamplerException) {
                MessageBox.Show(
                   this,
                   ThemeManager.GetString("dialogs.noresampler.message"),
                   ThemeManager.GetString("dialogs.noresampler.caption"),
                   MessageBox.MessageBoxButtons.Ok);
            } catch (Core.Render.NoWavtoolException) {
                MessageBox.Show(
                   this,
                   ThemeManager.GetString("dialogs.noresampler.message"),
                   ThemeManager.GetString("dialogs.noresampler.caption"),
                   MessageBox.MessageBoxButtons.Ok);
            } catch (Exception e) {
                MessageBox.ShowError(this, e);
            }
        }

        public void HScrollPointerWheelChanged(object sender, PointerWheelEventArgs args) {
            var scrollbar = (ScrollBar)sender;
            scrollbar.Value = Math.Max(scrollbar.Minimum, Math.Min(scrollbar.Maximum, scrollbar.Value - scrollbar.SmallChange * args.Delta.Y));
        }

        public void VScrollPointerWheelChanged(object sender, PointerWheelEventArgs args) {
            var scrollbar = (ScrollBar)sender;
            scrollbar.Value = Math.Max(scrollbar.Minimum, Math.Min(scrollbar.Maximum, scrollbar.Value - scrollbar.SmallChange * args.Delta.Y));
        }

        public void TimelinePointerWheelChanged(object sender, PointerWheelEventArgs args) {
            var control = (Control)sender;
            var position = args.GetCurrentPoint((Visual) sender).Position;
            var size = control.Bounds.Size;
            position = position.WithX(position.X / size.Width).WithY(position.Y / size.Height);
            viewModel.TracksViewModel.OnXZoomed(position, 0.1 * args.Delta.Y);
        }

        public void ViewScalerPointerWheelChanged(object sender, PointerWheelEventArgs args) {
            viewModel.TracksViewModel.OnYZoomed(new Point(0, 0.5), 0.1 * args.Delta.Y);
        }

        public void TimelinePointerPressed(object sender, PointerPressedEventArgs args) {
            var control = (Control)sender;
            var point = args.GetCurrentPoint(control);
            if (point.Properties.IsLeftButtonPressed) {
                args.Pointer.Capture(control);
                viewModel.TracksViewModel.PointToLineTick(point.Position, out int left, out int right);
                viewModel.PlaybackViewModel.MovePlayPos(left);
            } else if (point.Properties.IsRightButtonPressed) {
                int tick = viewModel.TracksViewModel.PointToTick(point.Position);
                viewModel.RefreshTimelineContextMenu(tick);
            }
        }

        public void TimelinePointerMoved(object sender, PointerEventArgs args) {
            var control = (Control)sender;
            var point = args.GetCurrentPoint(control);
            if (point.Properties.IsLeftButtonPressed) {
                viewModel.TracksViewModel.PointToLineTick(point.Position, out int left, out int right);
                viewModel.PlaybackViewModel.MovePlayPos(left);
            }
        }

        public void TimelinePointerReleased(object sender, PointerReleasedEventArgs args) {
            args.Pointer.Capture(null);
        }

        public void PartsCanvasPointerPressed(object sender, PointerPressedEventArgs args) {
            var control = (Control)sender;
            var point = args.GetCurrentPoint(control);
            var hitControl = control.InputHitTest(point.Position);
            if (partEditState != null) {
                return;
            }
            if (point.Properties.IsLeftButtonPressed) {
                if (args.KeyModifiers == cmdKey) {
                    partEditState = new PartSelectionEditState(control, viewModel, SelectionBox);
                    Cursor = ViewConstants.cursorCross;
                } else if (hitControl == control) {
                    viewModel.TracksViewModel.DeselectParts();
                    var part = viewModel.TracksViewModel.MaybeAddPart(point.Position);
                    if (part != null) {
                        // Start moving right away
                        partEditState = new PartMoveEditState(control, viewModel, part);
                        Cursor = ViewConstants.cursorSizeAll;
                    }
                } else if (hitControl is PartControl partControl) {
                    bool isVoice = partControl.part is UVoicePart;
                    bool isWave = partControl.part is UWavePart;
                    bool trim = point.Position.X > partControl.Bounds.Right - ViewConstants.ResizeMargin;
                    bool skip = point.Position.X < partControl.Bounds.Left + ViewConstants.ResizeMargin;
                    if (isVoice && trim) {
                        partEditState = new PartResizeEditState(control, viewModel, partControl.part);
                        Cursor = ViewConstants.cursorSizeWE;
                    } else if (isWave && skip) {
                        // TODO
                    } else if (isWave && trim) {
                        // TODO
                    } else {
                        partEditState = new PartMoveEditState(control, viewModel, partControl.part);
                        Cursor = ViewConstants.cursorSizeAll;
                    }
                }
            } else if (point.Properties.IsRightButtonPressed) {
                if (hitControl is PartControl partControl) {
                    if (!viewModel.TracksViewModel.SelectedParts.Contains(partControl.part)) {
                        viewModel.TracksViewModel.DeselectParts();
                        viewModel.TracksViewModel.SelectPart(partControl.part);
                    }
                    if (PartsContextMenu != null && viewModel.TracksViewModel.SelectedParts.Count > 0) {
                        PartsContextMenu.DataContext = new PartsContextMenuArgs {
                            Part = partControl.part,
                            PartDeleteCommand = viewModel.PartDeleteCommand,
                            PartReplaceAudioCommand = PartReplaceAudioCommand,
                            PartRenameCommand = PartRenameCommand,
                        };
                        shouldOpenPartsContextMenu = true;
                    }
                } else {
                    viewModel.TracksViewModel.DeselectParts();
                }
            } else if (point.Properties.IsMiddleButtonPressed) {
                partEditState = new PartPanningState(control, viewModel);
                Cursor = ViewConstants.cursorHand;
            }
            if (partEditState != null) {
                partEditState.Begin(point.Pointer, point.Position);
                partEditState.Update(point.Pointer, point.Position);
            }
        }

        public void PartsCanvasPointerMoved(object sender, PointerEventArgs args) {
            var control = (Control) sender;
            var point = args.GetCurrentPoint(control);
            if (partEditState != null) {
                partEditState.Update(point.Pointer, point.Position);
                return;
            }
            var hitControl = control.InputHitTest(point.Position);
            if (hitControl is PartControl partControl) {
                bool isVoice = partControl.part is UVoicePart;
                bool isWave = partControl.part is UWavePart;
                bool trim = point.Position.X > partControl.Bounds.Right - ViewConstants.ResizeMargin;
                bool skip = point.Position.X < partControl.Bounds.Left + ViewConstants.ResizeMargin;
                if (isVoice && trim) {
                    Cursor = ViewConstants.cursorSizeWE;
                } else if (isWave && (skip || trim)) {
                    Cursor = null; // TODO
                } else {
                    Cursor = null;
                }
            } else {
                Cursor = null;
            }
        }

        public void PartsCanvasPointerReleased(object sender, PointerReleasedEventArgs args) {
            if (partEditState != null) {
                if (partEditState.MouseButton != args.InitialPressMouseButton) {
                    return;
                }
                var control = (Control) sender;
                var point = args.GetCurrentPoint(control);
                partEditState.Update(point.Pointer, point.Position);
                partEditState.End(point.Pointer, point.Position);
                partEditState = null;
                Cursor = null;
            }
            if (openPianoRollWindow) {
                pianoRollWindow?.Show();
                pianoRollWindow?.Activate();
                openPianoRollWindow = false;
            }
        }

        public void PartsCanvasDoubleTapped(object sender, TappedEventArgs args) {
            if (!(sender is Canvas canvas)) {
                return;
            }
            var control = canvas.InputHitTest(args.GetPosition(canvas));
            if (control is PartControl partControl && partControl.part is UVoicePart) {
                if (pianoRollWindow == null) {
                    pianoRollWindow = new PianoRollWindow() {
                        MainWindow = this,
                    };
                    pianoRollWindow.ViewModel.PlaybackViewModel = viewModel.PlaybackViewModel;
                }
                // Workaround for new window losing focus.
                openPianoRollWindow = true;
                int tick = viewModel.TracksViewModel.PointToTick(args.GetPosition(canvas));
                DocManager.Inst.ExecuteCmd(new LoadPartNotification(partControl.part, DocManager.Inst.Project, tick));
                pianoRollWindow.AttachExpressions();
            }
        }

        public void PartsCanvasPointerWheelChanged(object sender, PointerWheelEventArgs args) {
            var delta = args.Delta;
            if (args.KeyModifiers == KeyModifiers.None || args.KeyModifiers == KeyModifiers.Shift) {
                if (args.KeyModifiers == KeyModifiers.Shift) {
                    delta = new Vector(delta.Y, delta.X);
                }
                if (delta.X != 0) {
                    HScrollBar.Value = Math.Max(HScrollBar.Minimum,
                        Math.Min(HScrollBar.Maximum, HScrollBar.Value - HScrollBar.SmallChange * delta.X));
                }
                if (delta.Y != 0) {
                    VScrollBar.Value = Math.Max(VScrollBar.Minimum,
                        Math.Min(VScrollBar.Maximum, VScrollBar.Value - VScrollBar.SmallChange * delta.Y));
                }
            } else if (args.KeyModifiers == KeyModifiers.Alt) {
                ViewScalerPointerWheelChanged(VScaler, args);
            } else if (args.KeyModifiers == cmdKey) {
                TimelinePointerWheelChanged(TimelineCanvas, args);
            }
            if (partEditState != null) {
                var point = args.GetCurrentPoint(partEditState.control);
                partEditState.Update(point.Pointer, point.Position);
            }
        }

        public void PartsContextMenuOpening(object sender, CancelEventArgs args) {
            if (shouldOpenPartsContextMenu) {
                shouldOpenPartsContextMenu = false;
            } else {
                args.Cancel = true;
            }
        }

        public void PartsContextMenuClosing(object sender, CancelEventArgs args) {
            if (PartsContextMenu != null) {
                PartsContextMenu.DataContext = null;
            }
        }

        void RenamePart(UPart part) {
            var dialog = new TypeInDialog();
            dialog.Title = ThemeManager.GetString("context.part.rename");
            dialog.SetText(part.name);
            dialog.onFinish = name => {
                if (!string.IsNullOrWhiteSpace(name) && name != part.name) {
                    if (!string.IsNullOrWhiteSpace(name) && name != part.name) {
                        DocManager.Inst.StartUndoGroup();
                        DocManager.Inst.ExecuteCmd(new RenamePartCommand(DocManager.Inst.Project, part, name));
                        DocManager.Inst.EndUndoGroup();
                    }
                }
            };
            dialog.ShowDialog(this);
        }

        async void ReplaceAudio(UPart part) {
            var file = await FilePicker.OpenFile(
                this, "context.part.replaceaudio", FilePicker.AudioFiles);
            if (file == null) {
                return;
            }
            UWavePart newPart = new UWavePart() {
                FilePath = file,
                trackNo = part.trackNo,
                position = part.position
            };
            newPart.Load(DocManager.Inst.Project);
            DocManager.Inst.StartUndoGroup();
            DocManager.Inst.ExecuteCmd(new ReplacePartCommand(DocManager.Inst.Project, part, newPart));
            DocManager.Inst.EndUndoGroup();
        }

        async void ValidateTracksVoiceColor() {
            DocManager.Inst.StartUndoGroup();
            foreach (var track in DocManager.Inst.Project.tracks) {
                if (track.ValidateVoiceColor(out var oldColors, out var newColors)) {
                    await VoiceColorRemappingAsync(track, oldColors, newColors);
                }
            }
            DocManager.Inst.EndUndoGroup();
        }
        async Task VoiceColorRemappingAsync(UTrack track, string[] oldColors, string[] newColors) {
            var parts = DocManager.Inst.Project.parts.Where(part => part.trackNo == track.TrackNo);
            if (parts.Any(part => part is UVoicePart vpart && vpart.notes.Count > 0)) {
                var dialog = new VoiceColorMappingDialog();
                VoiceColorMappingViewModel vm = new VoiceColorMappingViewModel(oldColors, newColors, track.TrackName);
                dialog.DataContext = vm;
                await dialog.ShowDialog(this);

                if (dialog.Apply) {
                    foreach (var part in parts) {
                        if (part is UVoicePart voicePart) {
                            foreach (var phoneme in voicePart.phonemes) {
                                var tuple = phoneme.GetExpression(DocManager.Inst.Project, track, Ustx.CLR);
                                if (vm.ColorMappings.Any(m => m.OldIndex == tuple.Item1)) {
                                    var mapping = vm.ColorMappings.First(m => m.OldIndex == tuple.Item1);
                                    if (mapping.OldIndex != mapping.SelectedIndex) {
                                        DocManager.Inst.ExecuteCmd(new SetPhonemeExpressionCommand(DocManager.Inst.Project, track, voicePart, phoneme, Ustx.CLR, mapping.SelectedIndex));
                                    }
                                } else {
                                    DocManager.Inst.ExecuteCmd(new SetPhonemeExpressionCommand(DocManager.Inst.Project, track, voicePart, phoneme, Ustx.CLR, 0));
                                }
                            }
                        }
                    }
                }
            }
        }
        void VoiceColorRemapping(UTrack track, string[] oldColors, string[] newColors) {
            var parts = DocManager.Inst.Project.parts.Where(part => part.trackNo == track.TrackNo);
            if (parts.Any(part => part is UVoicePart vpart && vpart.notes.Count > 0)) {
                var dialog = new VoiceColorMappingDialog();
                VoiceColorMappingViewModel vm = new VoiceColorMappingViewModel(oldColors, newColors, track.TrackName);
                dialog.DataContext = vm;
                dialog.onFinish = () => {
                    DocManager.Inst.StartUndoGroup();
                    foreach (var part in parts) {
                        if (part is UVoicePart voicePart) {
                            foreach (var phoneme in voicePart.phonemes) {
                                var tuple = phoneme.GetExpression(DocManager.Inst.Project, track, Ustx.CLR);
                                if (vm.ColorMappings.Any(m => m.OldIndex == tuple.Item1)) {
                                    var mapping = vm.ColorMappings.First(m => m.OldIndex == tuple.Item1);
                                    if (mapping.OldIndex != mapping.SelectedIndex) {
                                        DocManager.Inst.ExecuteCmd(new SetPhonemeExpressionCommand(DocManager.Inst.Project, track, voicePart, phoneme, Ustx.CLR, mapping.SelectedIndex));
                                    }
                                } else {
                                    DocManager.Inst.ExecuteCmd(new SetPhonemeExpressionCommand(DocManager.Inst.Project, track, voicePart, phoneme, Ustx.CLR, 0));
                                }
                            }
                        }
                    }
                    DocManager.Inst.EndUndoGroup();
                };
                dialog.ShowDialog(this);
            }
        }

        public async void WindowClosing(object? sender, WindowClosingEventArgs e) {
            if (forceClose || DocManager.Inst.ChangesSaved) {
                if (Preferences.Default.ClearCacheOnQuit) {
                    Log.Information("Clearing cache...");
                    PathManager.Inst.ClearCache();
                    Log.Information("Cache cleared.");
                }
                return;
            }
            e.Cancel = true;
            if (!await AskIfSaveAndContinue()) {
                return;
            }
            pianoRollWindow?.Close();
            forceClose = true;
            Close();
        }

        private async Task<bool> AskIfSaveAndContinue() {
            var result = await MessageBox.Show(
                this,
                ThemeManager.GetString("dialogs.exitsave.message"),
                ThemeManager.GetString("dialogs.exitsave.caption"),
                MessageBox.MessageBoxButtons.YesNoCancel);
            switch (result) {
                case MessageBox.MessageBoxResult.Yes:
                    await Save();
                    goto case MessageBox.MessageBoxResult.No;
                case MessageBox.MessageBoxResult.No:
                    return true; // Continue.
                default:
                    return false; // Cancel.
            }
        }

        public void OnNext(UCommand cmd, bool isUndo) {
            if (cmd is ErrorMessageNotification notif) {
                MessageBox.ShowError(this, notif.message, notif.e);
            } else if (cmd is VoiceColorRemappingNotification voicecolorNotif) {
                if(voicecolorNotif.TrackNo < 0 || DocManager.Inst.Project.tracks.Count <= voicecolorNotif.TrackNo) {
                    ValidateTracksVoiceColor();
                } else {
                    UTrack track = DocManager.Inst.Project.tracks[voicecolorNotif.TrackNo];
                    if (!voicecolorNotif.Validate) {
                        VoiceColorRemapping(track, track.VoiceColorNames, track.VoiceColorExp.options);
                    } else if (track.ValidateVoiceColor(out var oldColors, out var newColors)) {
                        VoiceColorRemapping(track, oldColors, newColors);
                    }
                }
            }
        }
    }
}
