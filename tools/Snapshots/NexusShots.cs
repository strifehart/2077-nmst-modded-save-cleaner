// Screenshots for the Nexus page: a 1440×1170 window (tall enough for a whole backup tile) rendered at 150%, like a capture on a 150% display.
// Works on a full copy of the saves folder in a temp folder, so renaming, cleaning and deleting never touch real saves.
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Nmst.SaveCleaner.Avalonia;
using Nmst.SaveCleaner.Core.Saves;
using Nmst.SaveCleaner.ViewModels;

static class NexusShots
{
    const double Scale = 1.5;
    static string _temp = "";

    public static void Run(string realSaves, string outDir)
    {
        outDir = Directory.CreateDirectory(outDir).FullName;
        var tempRoot = _temp = Directory.CreateTempSubdirectory("savebrowser-nexus-").FullName;
        try
        {
            var saves = Path.Combine(tempRoot, "Cyberpunk 2077");
            Console.WriteLine("copying saves…");
            Copy(realSaves, saves);

            AppBuilder.Configure<App>()
                .UseSkia()
                .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
                .SetupWithoutStarting();
            SynchronizationContext.SetSynchronizationContext(new AvaloniaSynchronizationContext());

            MainWindow.ForceCustomChrome = true;
            var w = new MainWindow { Width = 1440 * Scale, Height = 1170 * Scale };
            var vm = new MainViewModel(new AutoYes(), saves, Path.Combine(tempRoot, "Backups"), null, new StillPlayer())
                { MinBusyTime = TimeSpan.Zero };
            w.DataContext = vm;
            var content = (Control)w.Content!;
            w.Content = null;
            w.Content = new LayoutTransformControl { LayoutTransform = new ScaleTransform(Scale, Scale), Child = content };
            w.Show();
            w.UpdateChromeForSnapshot();
            w.HideIntro();

            vm.DisclaimerTicked = true;
            Run(vm.AcceptDisclaimerCommand.ExecuteAsync(null));
            WaitFor(() => !vm.StatusLine.Contains("checking") && vm.StatusLine.Length > 0, 120);
            vm.ClearLogCommand.Execute(null);   // the scan line names the temp folder

            vm.ExpandAllCommand.Execute(null);
            var groups = vm.Rows.OfType<PlaythroughGroupViewModel>().Where(g => !g.Playthrough.IsUngrouped).ToList();
            var main = groups.OrderByDescending(g => g.Items.Count).First();
            bool Nice(SaveItemViewModel s) => s.IsReadable && s.ScreenshotPath is not null && s.TrackedQuest != "No tracked quest";
            var tagged = main.Items.Where(s => s.IsTagged && Nice(s)).ToList();
            var hero = tagged[0];
            var batch = tagged.Take(4).ToList();
            var others = main.Items.Where(s => !batch.Contains(s) && Nice(s) && s.TrackedQuest != hero.TrackedQuest)
                .GroupBy(s => s.TrackedQuest).Select(g => g.First()).Take(2).ToList();

            // clean four saves as one batch (Original + Undo backups), then a manual backup of the first
            Check(vm, batch);
            Run(vm.CleanCheckedCommand.ExecuteAsync(null));
            vm.SelectNoneCommand.Execute(null);
            Focus(vm, hero.Name);
            Run(vm.BackupCheckedCommand.ExecuteAsync(null));

            // rename three, star four
            var names = new List<string>();
            foreach (var s in new[] { hero }.Concat(others))
            {
                var name = Valid("Before " + s.TrackedQuest);
                Focus(vm, s.Name);
                var t = vm.RenameFocusedCommand.ExecuteAsync(null);
                WaitFor(() => vm.RenameDialog is not null, 10);
                vm.RenameDialog!.Name = name;
                vm.RenameDialog.ConfirmCommand.Execute(null);
                Run(t);
                names.Add(name);
            }
            foreach (var n in names.Append(batch[2].Name)) vm.ToggleStarCommand.Execute(Find(vm, n));

            // three old saves from another playthrough go to the trash
            var trashed = groups.Where(g => g != main).OrderByDescending(g => g.Items.Count).First()
                .Items.Where(Nice).TakeLast(3).Select(s => s.Name).ToList();
            vm.ExpandAllCommand.Execute(null);
            Check(vm, trashed.Select(n => Find(vm, n)).ToList());
            Run(vm.DeleteCheckedCommand.ExecuteAsync(null));
            vm.SelectNoneCommand.Execute(null);

            Arrange(vm, main.Id, names[0]);
            ScrollTo(w, vm);
            Shot(w, vm, realSaves, outDir, "01_main");

            // a tagged save from another playthrough, with its sticky flag
            var moddedGroup = vm.Rows.OfType<PlaythroughGroupViewModel>().Where(g => !g.Playthrough.IsUngrouped && g.Id != main.Id)
                .OrderByDescending(g => g.Items.Count).First();
            var modded = moddedGroup.Items.FirstOrDefault(s => s.IsTagged && s.Sticky == true && Nice(s)) ?? moddedGroup.Items.First(s => s.IsTagged && Nice(s));
            Arrange(vm, moddedGroup.Id, modded.Name);
            ScrollTo(w, vm);
            Shot(w, vm, realSaves, outDir, "02_modded");

            Arrange(vm, main.Id, names[1]);
            ScrollTo(w, vm);
            var rename = vm.RenameFocusedCommand.ExecuteAsync(null);
            WaitFor(() => vm.RenameDialog is not null, 10);
            vm.RenameDialog!.Name = Valid(Find(vm, names[1]).TrackedQuest + ", take two");
            Shot(w, vm, realSaves, outDir, "03_rename");
            vm.RenameDialog.CancelCommand.Execute(null);
            Run(rename);

            vm.ShowStarredCommand.Execute(null);
            Focus(vm, names[0]);
            Shot(w, vm, realSaves, outDir, "04_starred");
            vm.ShowAllCommand.Execute(null);

            Arrange(vm, main.Id, names[0]);
            ScrollTo(w, vm);
            Run(vm.OpenTrashCommand.ExecuteAsync(null));
            vm.TrashView!.Rows[0].IsChecked = true;
            vm.TrashView.Rows[1].IsChecked = true;
            Shot(w, vm, realSaves, outDir, "05_trash");
            vm.TrashView.CloseCommand.Execute(null);

            Arrange(vm, main.Id, names[0]);
            // four tagged saves next to each other in the list, so all the ticks show
            var rows = vm.Rows.ToList();
            int start = Enumerable.Range(0, rows.Count - 3).First(i => rows.Skip(i).Take(4).All(r => r is SaveItemViewModel { IsTagged: true }));
            var picked = rows.Skip(start).Take(4).Cast<SaveItemViewModel>().ToList();
            Check(vm, picked);
            vm.FocusSaveCommand.Execute(picked[0]);   // so the list scrolls to them; ticking keeps the batch view
            Check(vm, picked);
            ScrollTo(w, vm);
            vm.IsLogOpen = true;
            Pump(); Thread.Sleep(400);
            Shot(w, vm, realSaves, outDir, "06_batch_log");
            vm.IsLogOpen = false;
            vm.SelectNoneCommand.Execute(null);
            Pump(); Thread.Sleep(400);

            Arrange(vm, main.Id, names[0]);
            ScrollTo(w, vm);
            var undo = vm.UndoLastBatchCommand.ExecuteAsync(null);
            WaitFor(() => vm.Review is not null, 30);
            Shot(w, vm, realSaves, outDir, "07_undo_review");
            vm.Review?.CancelCommand.Execute(null);
            Run(undo);

            Console.WriteLine("nexus shots in " + outDir);
        }
        finally
        {
            try { Directory.Delete(tempRoot, true); } catch (Exception e) { Console.WriteLine("couldn't remove " + tempRoot + ": " + e.Message); }
        }
    }

    static void Copy(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var f in Directory.GetFiles(from)) File.Copy(f, Path.Combine(to, Path.GetFileName(f)));
        foreach (var d in Directory.GetDirectories(from)) Copy(d, Path.Combine(to, Path.GetFileName(d)));
    }

    static string Valid(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c.ToString(), "");
        name = name.Replace("  ", " ").Trim().TrimEnd('.');
        return name.Length <= SaveRenamer.MaxLength ? name : name[..SaveRenamer.MaxLength].TrimEnd();
    }

    static SaveItemViewModel Find(MainViewModel vm, string name)
    {
        vm.ExpandAllCommand.Execute(null);
        return vm.Rows.OfType<SaveItemViewModel>().First(s => s.Name == name);
    }

    static void Focus(MainViewModel vm, string name) => vm.FocusSaveCommand.Execute(Find(vm, name));

    static void Check(MainViewModel vm, List<SaveItemViewModel> items)
    {
        vm.SelectNoneCommand.Execute(null);
        foreach (var i in items) Find(vm, i.Name).IsChecked = true;
        Pump();
    }

    /// <summary>Only the given playthrough open, with the given save focused.</summary>
    static void Arrange(MainViewModel vm, string? groupId, string focus)
    {
        var item = Find(vm, focus);
        vm.CollapseAllCommand.Execute(null);
        vm.Rows.OfType<PlaythroughGroupViewModel>().First(g => g.Id == groupId).ToggleExpandedCommand.Execute(null);
        vm.DismissResultCommand.Execute(null);
        vm.FocusSaveCommand.Execute(item);
        Pump();
    }

    /// <summary>Scrolls the save list so the focused save sits a few rows down.</summary>
    static void ScrollTo(Window w, MainViewModel vm)
    {
        var list = w.GetVisualDescendants().OfType<ItemsControl>().First(i => ReferenceEquals(i.ItemsSource, vm.Rows));
        int index = vm.Rows.IndexOf(vm.Focused!);
        var sv = list.GetVisualDescendants().OfType<ScrollViewer>().First();
        list.ScrollIntoView(index);   // the list is virtualised: rows off screen have no container until scrolled to
        Pump();
        var target = list.ContainerFromIndex(Math.Max(0, index - 2));
        if (target is not null) sv.Offset = new Vector(0, target.Bounds.Y);
        Pump();
    }

    static void Shot(Window w, MainViewModel vm, string realSaves, string outDir, string name)
    {
        // log lines that name the temp folder (the rescans after each change) go
        foreach (var e in vm.LogEntries.Where(e => e.Message.Contains(_temp)).ToList()) vm.LogEntries.Remove(e);
        typeof(CommunityToolkit.Mvvm.ComponentModel.ObservableObject).GetMethod("OnPropertyChanged", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance, [typeof(string)])!
            .Invoke(vm, [nameof(MainViewModel.LogCount)]);
        Pump();
        // the footer shows the real saves folder, not the temp copy
        foreach (var t in w.GetVisualDescendants().OfType<TextBlock>().Where(t => t.Text == vm.SavesFolder)) t.Text = realSaves;
        foreach (var b in w.GetVisualDescendants().OfType<Button>().Where(b => b.Content as string == "Use default")) b.IsVisible = false;
        Pump();
        w.CaptureRenderedFrame()?.Save(Path.Combine(outDir, name + ".png"));
        Console.WriteLine("  " + name);
    }

    static void Pump()
    {
        for (int i = 0; i < 20; i++) { Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Thread.Sleep(15); }
    }

    static void Run(Task t)
    {
        while (!t.IsCompleted) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(10); }
        t.GetAwaiter().GetResult();
    }

    static void WaitFor(Func<bool> done, int seconds)
    {
        var end = DateTime.Now.AddSeconds(seconds);
        while (!done() && DateTime.Now < end) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(25); }
    }

    /// <summary>Says yes to every confirmation, so the flows run without dialogs.</summary>
    sealed class AutoYes : IPlatformServices
    {
        public Task<bool> ConfirmAsync(string title, string message, string confirmText, string? warning = null) => Task.FromResult(true);
        public Task AlertAsync(string title, string message) => Task.CompletedTask;
        public Task<string?> PickFolderAsync(string title, string? startFolder) => Task.FromResult<string?>(null);
        public void OpenFolder(string path) { }
        public void OpenUrl(string url) { }
        public void CopyToClipboard(string text) { }
        public void Quit() { }
    }
}
