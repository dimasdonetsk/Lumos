using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Navigation;

namespace Lumos
{
    /// <summary>Вікно «Підключити EcoFlow»: ключі розробника або логін застосунку.</summary>
    public partial class EcoFlowWindow : Window
    {
        private readonly AppController _app;
        private bool _keysMode = true;
        private string? _keysHost;

        internal EcoFlowWindow(AppController app)
        {
            _app = app;
            InitializeComponent();
            Settings s = app.Settings;
            _keysMode = s.EcoMode != "Login";
            AccessKeyBox.Text = s.EcoAccessKey ?? "";
            SecretKeyBox.Password = EcoFlow.Unprotect(s.EcoSecretEnc);
            EmailBox.Text = s.EcoEmail ?? "";
            PasswordInput.Password = EcoFlow.Unprotect(s.EcoPasswordEnc);
            SnBox.Text = s.EcoMode == "Login" ? s.EcoSn ?? "" : "";
            if (app.EcoConnected)
            {
                DisconnectButton.Visibility = Visibility.Visible;
                ConnectButton.Content = "Зберегти";
                ShowStatus(true, "Підключено: " + (s.EcoName ?? s.EcoSn) + (app.Station is StationStatus st ? " · " + st.Soc + "%" : ""));
            }
            ApplyMode();
        }

        private void ApplyMode()
        {
            KeysPanel.Visibility = _keysMode ? Visibility.Visible : Visibility.Collapsed;
            LoginPanel.Visibility = _keysMode ? Visibility.Collapsed : Visibility.Visible;
            KeysTab.Background = _keysMode ? Theme.Get("TabOn") : System.Windows.Media.Brushes.Transparent;
            LoginTab.Background = _keysMode ? System.Windows.Media.Brushes.Transparent : Theme.Get("TabOn");
            KeysTab.Foreground = _keysMode ? Theme.Get("Fg") : Theme.Get("Fg2");
            LoginTab.Foreground = _keysMode ? Theme.Get("Fg2") : Theme.Get("Fg");
            KeysTab.FontWeight = _keysMode ? FontWeights.SemiBold : FontWeights.Normal;
            LoginTab.FontWeight = _keysMode ? FontWeights.Normal : FontWeights.SemiBold;
            DevicesPanel.Visibility = Visibility.Collapsed;
        }

        private void OnKeysTab(object sender, RoutedEventArgs e) { _keysMode = true; ApplyMode(); }
        private void OnLoginTab(object sender, RoutedEventArgs e) { _keysMode = false; ApplyMode(); }

        private void ShowStatus(bool ok, string text)
        {
            StatusText.Text = text;
            StatusText.Foreground = ok ? Theme.StatusGreen : Theme.StatusRed;
            StatusText.Visibility = Visibility.Visible;
        }

        private async void OnConnect(object sender, RoutedEventArgs e)
        {
            ConnectButton.IsEnabled = false;
            ShowStatus(true, "Підключаюсь…");
            StatusText.Foreground = Theme.Get("Fg2");
            try
            {
                if (_keysMode)
                {
                    string ak = AccessKeyBox.Text.Trim(), sk = SecretKeyBox.Password.Trim();
                    if (ak.Length == 0 || sk.Length == 0) { ShowStatus(false, "Встав обидва ключі."); return; }
                    var (host, devices) = await EcoFlow.ListDevicesAsync(ak, sk);
                    _keysHost = host;
                    if (devices.Count == 0) { ShowStatus(false, "Ключі підійшли, але на акаунті немає станцій. Додай станцію в застосунку EcoFlow."); return; }
                    if (devices.Count == 1) { await SaveKeysAsync(ak, sk, devices[0]); return; }
                    ShowDevices(ak, sk, devices);
                    ShowStatus(true, "Ключі підійшли. Обери станцію.");
                }
                else
                {
                    string email = EmailBox.Text.Trim(), pw = PasswordInput.Password, sn = SnBox.Text.Trim().ToUpperInvariant();
                    if (email.Length == 0 || pw.Length == 0) { ShowStatus(false, "Введи пошту й пароль EcoFlow."); return; }
                    if (sn.Length < 8) { ShowStatus(false, "Введи серійний номер станції."); return; }
                    EcoFlow.ForgetSession();
                    StationStatus st = await EcoFlow.QuotaByLoginAsync(email, pw, sn);
                    _app.SetStationConnection(s =>
                    {
                        s.EcoMode = "Login"; s.EcoEmail = email; s.EcoPasswordEnc = EcoFlow.Protect(pw); s.EcoSn = sn;
                        s.EcoName = "EcoFlow " + sn.Substring(0, Math.Min(3, sn.Length)) + "…" + sn.Substring(Math.Max(0, sn.Length - 4));
                        s.EcoAccessKey = null; s.EcoSecretEnc = null; s.EcoHost = null;
                    });
                    Done(st);
                }
            }
            catch (EcoFlow.EcoException ex) { ShowStatus(false, ex.Message); }
            catch (Exception ex)
            {
                Log.Write("EcoFlow connect: " + ex);
                ShowStatus(false, "Не вдалося з'єднатися з EcoFlow. Перевір інтернет і спробуй ще.");
            }
            finally { ConnectButton.IsEnabled = true; }
        }

        private void ShowDevices(string ak, string sk, List<EcoFlow.Device> devices)
        {
            DevicesList.Items.Clear();
            foreach (EcoFlow.Device d in devices)
            {
                var b = new Button
                {
                    Content = d.Name + (d.Online ? "" : " · не в мережі"),
                    Style = (Style)FindResource("Ghost"),
                    Margin = new Thickness(0, 0, 0, 6),
                    HorizontalContentAlignment = HorizontalAlignment.Left,
                };
                b.Click += async (_, _) =>
                {
                    try { await SaveKeysAsync(ak, sk, d); }
                    catch (EcoFlow.EcoException ex) { ShowStatus(false, ex.Message); }
                    catch (Exception ex) { Log.Write("EcoFlow pick: " + ex); ShowStatus(false, "Не вдалося отримати дані станції."); }
                };
                DevicesList.Items.Add(b);
            }
            DevicesPanel.Visibility = Visibility.Visible;
        }

        private async System.Threading.Tasks.Task SaveKeysAsync(string ak, string sk, EcoFlow.Device device)
        {
            StationStatus st = await EcoFlow.QuotaByKeysAsync(_keysHost ?? EcoFlow.HostEu, ak, sk, device.Sn);
            _app.SetStationConnection(s =>
            {
                s.EcoMode = "Keys"; s.EcoHost = _keysHost; s.EcoAccessKey = ak; s.EcoSecretEnc = EcoFlow.Protect(sk);
                s.EcoSn = device.Sn; s.EcoName = device.Name;
                s.EcoEmail = null; s.EcoPasswordEnc = null;
            });
            Done(st);
        }

        private void Done(StationStatus st)
        {
            ShowStatus(true, "Готово: заряд " + st.Soc + "%. Станція тепер у картці Lumos.");
            DisconnectButton.Visibility = Visibility.Visible;
            ConnectButton.Content = "Зберегти";
            DevicesPanel.Visibility = Visibility.Collapsed;
        }

        private void OnDisconnect(object sender, RoutedEventArgs e)
        {
            _app.DisconnectStation();
            Close();
        }

        private void OnLink(object sender, RequestNavigateEventArgs e) => _app.OpenUrl(e.Uri.AbsoluteUri);
        private void OnClose(object sender, RoutedEventArgs e) => Close();

        private void OnDrag(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left && e.OriginalSource is not TextBox && e.OriginalSource is not PasswordBox)
                try { DragMove(); } catch { }
        }
    }
}
