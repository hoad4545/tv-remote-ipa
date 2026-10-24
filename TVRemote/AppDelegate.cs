using Foundation;
using UIKit;

namespace TVRemote;

[Register("AppDelegate")]
public class AppDelegate : UIApplicationDelegate {
    public override UIWindow? Window {
        get;
        set;
    }

    public override bool FinishedLaunching(UIApplication application, NSDictionary launchOptions) {
        // The PC's ADB key, already trusted by this TV: the app signs auth tokens with it and
        // connects silently — no "Allow debugging?" popup on the TV.
        var keyBlob = typeof(RsaSha).Assembly.GetManifestResourceStream("TVRemote.adbkey_p8.b64.txt");
        using var reader = new StreamReader(keyBlob!);
        RsaSha.Load(reader.ReadToEnd());

        Window = new UIWindow(UIScreen.MainScreen.Bounds);
        Window.RootViewController = new MainTabs();
        Window.MakeKeyAndVisible();
        return true;
    }
}

/// <summary>Dark tab bar: Пульт, Оптимизация, Обои, Приложения.</summary>
public sealed class MainTabs : UITabBarController {
    public MainTabs() {
        ViewControllers = new UIViewController[] {
            new RemotePage("Пульт"),
            new SystemPage("Оптимизация"),
            new WallpaperPage("Обои"),
            new AppsPage("Приложения"),
        };
        TabBar.BackgroundColor = UIColor.FromRGB(0x14, 0x18, 0x20);
        TabBar.UnselectedItemTintColor = UIColor.FromRGB(0x62, 0x68, 0x72);
        TabBar.TintColor = UIColor.FromRGB(0xb9, 0xe7, 0xc7);
    }
}

/// <summary>Shared dark page base: a vertical stack inside a scroll plus a status line.</summary>
public class TvPage : UIViewController {
    protected readonly UIScrollView Scroll = new();
    protected readonly UIStackView Stack = new();
    protected readonly UILabel Status = new() {
        TextAlignment = UITextAlignment.Center,
        Font = UIFont.SystemFontOfSize(12),
        TextColor = UIColor.FromRGB(0x8b, 0x90, 0x99),
        Lines = 2,
        Text = "",
    };
    protected static readonly AdbClient Adb = new();

    public TvPage(string title) {
        Title = title;
        View!.BackgroundColor = UIColor.FromRGB(0x0d, 0x10, 0x14);

        Stack.Axis = UILayoutConstraintAxis.Vertical;
        Stack.Spacing = 10;
        Stack.TranslatesAutoresizingMaskIntoConstraints = false;
        Scroll.TranslatesAutoresizingMaskIntoConstraints = false;
        Scroll.AlwaysBounceVertical = true;
        Scroll.AddSubview(Stack);
        View.AddSubview(Scroll);
        View.AddSubview(Status);
        Status.TranslatesAutoresizingMaskIntoConstraints = false;

        NSLayoutConstraint.ActivateConstraints(new[] {
            Scroll.LeadingAnchor.ConstraintEqualTo(View.LeadingAnchor),
            Scroll.TrailingAnchor.ConstraintEqualTo(View.TrailingAnchor),
            Scroll.TopAnchor.ConstraintEqualTo(View.SafeAreaLayoutGuide.TopAnchor, 2),
            Scroll.BottomAnchor.ConstraintEqualTo(Status.TopAnchor, -6),

            Stack.LeadingAnchor.ConstraintEqualTo(Scroll.FrameLayoutGuide.LeadingAnchor, 18),
            Stack.TrailingAnchor.ConstraintEqualTo(Scroll.FrameLayoutGuide.TrailingAnchor, -18),
            Stack.TopAnchor.ConstraintEqualTo(Scroll.ContentLayoutGuide.TopAnchor, 14),
            Stack.BottomAnchor.ConstraintEqualTo(Scroll.ContentLayoutGuide.BottomAnchor, -28),

            Status.LeadingAnchor.ConstraintEqualTo(View.LeadingAnchor, 14),
            Status.TrailingAnchor.ConstraintEqualTo(View.TrailingAnchor, -14),
            Status.BottomAnchor.ConstraintEqualTo(View.SafeAreaLayoutGuide.BottomAnchor, -4),
        });
    }

    /// <summary>Rounded frosted action button of the fixed height; the caller wires the tap.</summary>
    protected static UIButton GlassButton(string title, float height) {
        var button = new UIButton(UIButtonType.System) {
            BackgroundColor = UIColor.FromRGBA(1, 1, 1, 0.07f),
            TranslatesAutoresizingMaskIntoConstraints = false,
        };
        button.SetTitle(title, UIControlState.Normal);
        button.SetTitleColor(UIColor.FromRGB(0xdd, 0xe2, 0xe7), UIControlState.Normal);
        button.TitleLabel!.Font = UIFont.SystemFontOfSize(15);
        button.Layer.CornerRadius = 12;
        button.Layer.BorderColor = UIColor.FromWhiteAlpha(1, 0.14f).CGColor;
        button.Layer.BorderWidth = 0.5f;
        button.HeightAnchor.ConstraintEqualTo(height).Active = true;
        return button;
    }

    protected static UILabel Hint(string text) => new() {
        Text = text,
        Font = UIFont.SystemFontOfSize(11),
        Lines = 0,
        TextColor = UIColor.FromRGB(0x66, 0x6b, 0x74),
    };

    protected void SetStatus(string text) =>
        InvokeOnMainThread(() => Status.Text = text.Length == 0 ? "Готово." : text);
}

/// <summary>D-pad diamond plus the most-used keys of the TCL remote.</summary>
public sealed class RemotePage : TvPage {

    readonly UIView _grid = new() {
        BackgroundColor = UIColor.FromRGBA(1, 1, 1, 0.04f),
        TranslatesAutoresizingMaskIntoConstraints = false,
    };

    public RemotePage(string title) : base(title) {
        _grid.Layer.CornerRadius = 18;
        _grid.Layer.BorderWidth = 0.5f;
        _grid.Layer.BorderColor = UIColor.FromWhiteAlpha(1, 0.14f).CGColor;
        Stack.AddArrangedSubview(_grid);
        _grid.HeightAnchor.ConstraintEqualTo(238).Active = true;

        _up = Pad("▲", 19, 54); _down = Pad("▼", 20, 54);
        _left = Pad("◀", 21, 54); _right = Pad("▶", 22, 54);
        _ok = Pad("OK", 23, 74, UIColor.FromRGB(0xb9, 0xe7, 0xc7), 16);
        _grid.AddSubview(_up!); _grid.AddSubview(_down!);
        _grid.AddSubview(_left!); _grid.AddSubview(_right!);
        _grid.AddSubview(_ok!);

        Button("Home", 3);
        Button("Назад", 4);
        Button("Меню (≡)", 82);
        Button("Громкость +", 24);
        Button("Громкость −", 25);
        Button("Без звука", 164);
        Button("Питание (сон / пробуждение)", 26);
    }

    readonly UIButton? _up, _down, _left, _right, _ok;

    /// <summary>Diamond layout around OK, computed for the real width of the card.</summary>
    public override void ViewDidLayoutSubviews() {
        base.ViewDidLayoutSubviews();
        var b = _grid.Bounds;
        float cx = (float)(b.Width / 2), okY = (float)b.Height / 2 - 10;
        _up!.Frame = new CGRect(cx - 27, okY - 74, 54, 54);
        _down!.Frame = new CGRect(cx - 27, okY + 74, 54, 54);
        _ok!.Frame = new CGRect(cx - 37, okY - 37, 74, 74);
        _left!.Frame = new CGRect(cx - 115, okY - 27, 54, 54);
        _right!.Frame = new CGRect(cx + 115 - 54, okY - 27, 54, 54);
    }

    UIButton Pad(string title, int code, float size, UIColor? tint = null, float fontSize = 19) {
        var button = new UIButton(UIButtonType.System);
        button.SetTitle(title, UIControlState.Normal);
        button.TitleLabel!.Font = UIFont.BoldSystemFontOfSize(fontSize);
        button.SetTitleColor(tint ?? UIColor.FromRGB(0xe8, 0xec, 0xf1), UIControlState.Normal);
        button.BackgroundColor = UIColor.FromRGBA(1, 1, 1, 0.10f);
        button.Layer.CornerRadius = size / 2;
        button.TouchUpInside += async (_, _) => await Send(code);
        return button;
    }

    void Button(string title, int code) {
        var button = GlassButton(title, 44);
        button.TouchUpInside += async (_, _) => await Send(code);
        Stack.AddArrangedSubview(button);
    }

    async Task Send(int code) {
        SetStatus("→ keyevent " + code);
        try {
            await Adb.ShellAsync("input keyevent " + code);
            SetStatus("✓");
        } catch (Exception e) {
            SetStatus("ТВ не отвечает: " + e.Message);
        }
    }
}

/// <summary>Memory optimizer and the TV screenshot door.</summary>
public sealed class SystemPage : TvPage {
    readonly UILabel _ram = new() {
        Text = "Память ТВ: …",
        Font = UIFont.BoldSystemFontOfSize(15),
        TextColor = UIColor.FromRGB(0xdd, 0xe2, 0xe7),
        TextAlignment = UITextAlignment.Center,
    };
    readonly UIImageView _preview = new() {
        ContentMode = UIViewContentMode.ScaleAspectFit,
        BackgroundColor = UIColor.FromRGB(0x10, 0x14, 0x19),
        Hidden = true,
        ClipsToBounds = true,
    };
    byte[]? _screenshot;

    public SystemPage(string title) : base(title) {
        _preview.Layer.CornerRadius = 12;
        _preview.Layer.BorderWidth = 0.5f;
        _preview.Layer.BorderColor = UIColor.FromWhiteAlpha(1, 0.12f).CGColor;

        Stack.AddArrangedSubview(_ram);
        Stack.AddArrangedSubview(Button("Оптимизировать память (закрыть фоновые)", 48, Kill));
        Stack.AddArrangedSubview(Button("Очистить кэш приложений", 44, Trim));
        Stack.AddArrangedSubview(Hint("«Оптимизировать» завершает приложения, оставшиеся в памяти после сна (кроме того, что играет прямо сейчас)."));
        Stack.AddArrangedSubview(Button("Сделать скриншот ТВ", 44, Screenshot));
        Stack.AddArrangedSubview(Button("Сохранить скриншот в «Фото»", 44, SaveShot));
        _preview.HeightAnchor.ConstraintEqualTo(170).Active = true;
        Stack.AddArrangedSubview(_preview);

        _ = RefreshRam();
    }

    static UIButton Button(string title, float height, Func<Task> action) {
        var button = GlassButton(title, height);
        button.TouchUpInside += async (_, _) => {
            button.Enabled = false;
            try { await action(); }
            catch { }
            finally { button.Enabled = true; }
        };
        return button;
    }

    async Task RefreshRam() {
        try {
            string mem = await Adb.ShellAsync("cat /proc/meminfo | grep -e MemTotal -e MemAvailable");
            long total = Parse(mem, "MemTotal"), free = Parse(mem, "MemAvailable");
            InvokeOnMainThread(() => _ram.Text =
                $"Память ТВ: свободно {free / 1024.0 / 1024.0:0.0} ГБ из {total / 1024.0 / 1024.0:0.0} ГБ");
        } catch (Exception e) {
            InvokeOnMainThread(() => _ram.Text = "Память ТВ: нет связи (" + e.Message + ")");
        }
        static long Parse(string text, string key) {
            foreach (var line in text.Split('\n')) {
                if (!line.Contains(key)) continue;
                var digits = new string(line.Where(char.IsDigit).ToArray());
                long.TryParse(digits, out long value);
                return value;
            }
            return 0;
        }
    }

    async Task Kill() {
        SetStatus("Закрываю фоновые приложения…");
        await Adb.ShellAsync("am kill-all");
        await Task.Delay(600);
        SetStatus("Фоновые приложения закрыты.");
        await RefreshRam();
    }

    async Task Trim() {
        SetStatus("Чищу кэш…");
        await Adb.ShellAsync("pm trim-caches 9999999999G");
        SetStatus("Кэш очищен.");
        await RefreshRam();
    }

    async Task Screenshot() {
        SetStatus("Снятие экрана ТВ…");
        try {
            _screenshot = await Adb.ExecAsync("screencap -p");
            InvokeOnMainThread(() => {
                _preview.Image = UIImage.LoadFromData(NSData.FromArray(_screenshot));
                _preview.Hidden = _preview.Image == null;
                SetStatus(_preview.Image != null ? "Скриншот готов." : "ТВ ответило не картинкой (может, он спит).");
            });
        } catch (Exception e) {
            SetStatus("Не вышло: " + e.Message);
        }
    }

    async Task SaveShot() {
        if (_screenshot is not { Length: > 16 }) {
            SetStatus("Сначала сделайте скриншот.");
            return;
        }
        await Task.Yield();
        var image = UIImage.LoadFromData(NSData.FromArray(_screenshot));
        if (image == null) { SetStatus("Картинка не распознана."); return; }
        image.SaveToPhotosAlbum((saved, error) =>
            SetStatus(error == null ? "Скриншот в «Фото»." : "Не сохранился: " + error.LocalizedDescription));
    }
}

/// <summary>Wallpaper rotation: any photo from «Фото» lands in the launcher's own theme set.</summary>
public sealed class WallpaperPage : TvPage {

    public WallpaperPage(string title) : base(title) {
        Stack.AddArrangedSubview(Button("Выбрать фото и поставить обоем", 48, PickStart));
        Stack.AddArrangedSubview(Button("Перелистнуть обои сейчас", 44, Shuffle));
        Stack.AddArrangedSubview(Hint("Фото уходит в коллекцию обоев лаунчера и подхватывается сразу — следующий кадр ротации покажет его. Тонкая настройка обоев (цвет, затемнение) — в приложении «Обои» на ТВ."));
        Stack.AddArrangedSubview(Hint("ТВ должно быть включено и в одной Wi-Fi сети с iPhone (192.168.1.232)."));
    }

    static UIButton Button(string title, float height, Func<Task> action) {
        var button = GlassButton(title, height);
        button.TouchUpInside += async (_, _) => {
            button.Enabled = false;
            try { await action(); }
            catch { }
            finally { button.Enabled = true; }
        };
        return button;
    }

    async Task PickStart() {
        await Task.Yield();
        var picker = new UIImagePickerController {
            SourceType = UIImagePickerControllerSourceType.PhotoLibrary,
            MediaTypes = new[] { "public.image" },
        };
        picker.Delegate = new ImagePickerDelegate(async image => {
            if (image == null) return;
            NSData jpeg = image.AsJPEG(0.9f);
            SetStatus("Загрузка обоев в ТВ…");
            string tmp = "/data/local/tmp/cytv-wallpaper.jpg";
            try {
                await Adb.PushAsync(jpeg.ToArray(), tmp);
                string name = "wall_" + DateTimeOffset.UtcNow.ToUnixTimeSeconds() + ".jpg";
                await Adb.ShellAsync("content write --uri content://com.gathin.tvhome.theme/wallpapers/" + name + " < " + tmp);
                await Adb.ShellAsync("rm -f " + tmp);
                SetStatus("Обои поставлены: " + name);
            } catch (Exception e) {
                SetStatus("Не удалось: " + e.Message);
            }
        });
        PresentViewController(picker, true, null);
    }

    async Task Shuffle() {
        SetStatus("Меняю обои…");
        try {
            await Adb.ShellAsync("content call --uri content://com.gathin.tvhome.theme --method next-wallpaper");
            SetStatus("Смена обоев отдана.");
        } catch (Exception e) {
            SetStatus("Не вышло: " + e.Message);
        }
    }

    sealed class ImagePickerDelegate : UIImagePickerControllerDelegate {
        readonly Action<UIImage?> _picked;
        public ImagePickerDelegate(Action<UIImage?> picked) { _picked = picked; }
        public override void FinishedPickingMedia(UIImagePickerController picker, NSDictionary info) {
            picker.DismissViewController(true, null);
            _picked(info?[UIImagePickerController.OriginalImage] as UIImage);
        }
        public override void Canceled(UIImagePickerController picker) {
            picker.DismissViewController(true, null);
            _picked(null);
        }
    }
}

/// <summary>Third-party apps with launch and uninstall.</summary>
public sealed class AppsPage : TvPage, IUITableViewDataSource, IUITableViewDelegate {

    string[] _packages = Array.Empty<string>();
    readonly UITableView _table;

    public AppsPage(string title) : base(title) {
        _table = new UITableView {
            TranslatesAutoresizingMaskIntoConstraints = false,
            BackgroundColor = UIColor.Clear,
            SeparatorColor = UIColor.FromWhiteAlpha(1, 0.08f),
            RowHeight = 46,
        };
        _table.DataSource = this;
        _table.Delegate = this;
        Stack.AddArrangedSubview(_table);
        _table.HeightAnchor.ConstraintEqualTo(430).Active = true;

        var refresh = GlassButton("Обновить список", 40);
        refresh.TouchUpInside += async (_, _) => await Load();
        Stack.AddArrangedSubview(refresh);
        Stack.AddArrangedSubview(Hint("«Открыть» запускает приложение на ТВ. Тап по строке — меню с удалением."));

        _ = Load();
    }

    async Task Load() {
        SetStatus("Читаю приложения…");
        try {
            string raw = await Adb.ShellAsync("pm list packages -3");
            _packages = raw.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.Replace("package:", "").Trim())
                .Where(p => p.Length > 0)
                .OrderBy(p => p, StringComparer.Ordinal)
                .ToArray();
            SetStatus("Приложений: " + _packages.Length);
            InvokeOnMainThread(() => _table.ReloadData());
        } catch (Exception e) {
            SetStatus("Нет связи: " + e.Message);
        }
    }

    public nint RowsInSection(UITableView tableView, nint section) => _packages.Length;

    public UITableViewCell GetCell(UITableView tableView, NSIndexPath indexPath) {
        var cell = tableView.DequeueReusableCell("pkg") ?? new UITableViewCell(UITableViewCellStyle.Default, "pkg");
        var pkg = _packages[indexPath.Row];
        cell.TextLabel!.Text = pkg;
        cell.TextLabel.TextColor = UIColor.FromRGB(0xdd, 0xe2, 0xe7);
        cell.BackgroundColor = UIColor.Clear;
        cell.SelectionStyle = UITableViewCellSelectionStyle.None;
        cell.AccessoryView = Launch(pkg);
        return cell;
    }

    UIButton Launch(string pkg) {
        var open = new UIButton(UIButtonType.System) { Frame = new CGRect(0, 0, 76, 30) };
        open.SetTitle("Открыть", UIControlState.Normal);
        open.TitleLabel!.Font = UIFont.SystemFontOfSize(12);
        open.SetTitleColor(UIColor.FromRGB(0xb9, 0xe7, 0xc7), UIControlState.Normal);
        open.Layer.BorderColor = UIColor.FromRGB(0xb9, 0xe7, 0xc7).CGColor;
        open.Layer.BorderWidth = 0.8f;
        open.Layer.CornerRadius = 14;
        open.TouchUpInside += async (_, _) => {
            SetStatus("Запуск " + pkg + "…");
            try { await Adb.ShellAsync("monkey -p " + pkg + " 1"); SetStatus("✓"); }
            catch (Exception e) { SetStatus("Не запустился: " + e.Message); }
        };
        return open;
    }

    [Export("tableView:didSelectRowAtIndexPath:")]
    public void RowSelected(UITableView tableView, NSIndexPath indexPath) {
        var pkg = _packages[indexPath.Row];
        var alert = UIAlertController.Create(pkg, null, UIAlertControllerStyle.Alert);
        alert.AddAction(UIAlertAction.Create("Удалить", UIAlertActionStyle.Destructive, async _ => {
            SetStatus("Удаление " + pkg + "…");
            try {
                string outText = await Adb.ShellAsync("pm uninstall " + pkg);
                SetStatus(outText.Contains("Success") ? "Удалено." : "Ответ ТВ: " + outText);
                await Load();
            } catch (Exception e) {
                SetStatus("Не удалилось: " + e.Message);
            }
        }));
        alert.AddAction(UIAlertAction.Create("Отмена", UIAlertActionStyle.Cancel, null));
        PresentViewController(alert, true, null);
    }
}
