# Lumos — нотатки для Claude

Трей-програма для Windows: графіки відключень світла по всіх областях України, аварійні відключення, заряд EcoFlow.
Репозиторій: https://github.com/dimasdonetsk/Lumos (публічний). Автор: Діма. Спілкуємося українською, коротко, без зайвого.

## Стек і збирання
- C# .NET 8, WPF + WinForms (NotifyIcon, ContextMenuStrip з власним DarkMenuRenderer). Проєкт `src/Lumos.csproj`, тести `tests/Lumos.Tests` (xUnit).
- Локально: `dotnet build src/Lumos.csproj -c Release`, `dotnet test tests/Lumos.Tests`. Або `build.bat`.
- Встановлення й автооновлення — Velopack 1.2.161. Власний `Main` у `Program.cs` (EnableDefaultApplicationDefinition=false, App.xaml як Page).
- **Реліз**: змінити `<Version>` у `src/Lumos.csproj` і запушити в main. `.github/workflows/build.yml` тестує, пакує `vpk` і створює GitHub-реліз `vX.Y.Z`. Без зміни версії реліз не створюється (а повтор тієї самої версії падає на кроці «Create GitHub release»).
- Коміти — лише коли Діма просить або як частина домовленої задачі; повідомлення комітів англійською, коротко.

## Структура
- `AppController.cs` — таймери (тік 30 с, дані 5 хв, оновлення 2 хв → 6 год, EcoFlow 3 хв / 45 с при відкритій картці), статус, ручні позначки «Вимкнули/Увімкнули», сповіщення.
- `OverlayWindow.xaml(.cs)` + `OverlayViewModel.cs` — картка над треєм (400 px): чип черги, велике число (скільки триває стан), вкладки Сьогодні/Завтра, смуга графіка, «Далі», блок EcoFlow, футер. Вікно створюється при відкритті й закривається при втраті фокусу (економія пам'яті, `MemoryHelper.Trim`).
- `TrayController.cs` — чорне меню й іконки (блискавка / перекреслена / червоний трикутник).
- `Theme.cs` — темна OLED (Card #0E0E11, Soft #18181C, TabOn #2B2B32) і світла «морозна» (Card #EEF3F9, TabOn білий). Кольори клітинок: зелений #36C66A, жовтий #F2C14E, червоний #F0574D. Шрифт «Segoe UI» (не Variable — WPF криво міряє).
- Джерела даних:
  - `ScheduleData.cs` — агрегатор `chaichuk/UA-power-outages-monitor` (ETag).
  - `ExtraSources.cs` — Вінниця/Одеса/Дніпро/Київщина (DTEK-дзеркала), Миколаїв (API), Кіровоград (Telegram), Волинь (PNG, `VolynImage.cs`).
  - `UzSource.cs` — Укрзалізниця (HTML-таблиці uz.gov.ua). Сайт УЗ не відповідає серверам GitHub (закордон), тому УЗ програма бере напряму.
  - `EmergencyMonitor.cs` — аварійні з Telegram (Укренерго, ДТЕК, обленерго), вікно 24 год до скасування; пости з опублікованим погодинним графіком не вважаються аварійними.
  - `CentralData.cs` — спільний збирач: `.github/workflows/collect.yml` кожні 5 хв запускає `Lumos.exe --collect data.json` і кладе в гілку `data`. Програма бере його, якщо свіжіший за 20 хв, інакше — напряму.
- `EcoFlow.cs` + `EcoFlowWindow.xaml` — EcoFlow через хмару: ключі розробника (HMAC-SHA256, `/iot-open/sign/device/quota/all`) або логін застосунку (MQTT `latestQuotas`). Поля River 2 Pro: `pd.soc`, `inv.inputWatts`, `mppt.inWatts`, `pd.wattsOutSum`, `bms_emsStatus.dsgRemainTime`, `bms_bmsStatus.remainTime` (5999 = невідомо). Секрети — DPAPI. Нічний режим: після обраної години (0–3) без струму від розетки 2 опитування поспіль → `acOutCfg` (moduleType 5) enabled=0; коли світло повертається — enabled=1 (лише якщо вимикав Lumos). **На справжній станції ще не перевірено.**
- `Settings.cs` — `%AppData%\Lumos\settings.json`; автозапуск через `HKCU\...\Run` з `--autostart`, перевірка заборони в `StartupApproved\Run`. Журнал: `%AppData%\Lumos\log.txt`.

## Відкриті питання
- Автозапуск після ввімкнення ПК не працював — у 0.13.2 переписано (шлях `current\Lumos.exe`, зняття заборони Windows, журнал запуску). Перевірити на ПК Діми: реєстр Run, Диспетчер завдань → Автозавантаження, `log.txt`.
- EcoFlow: перевірити на River 2 Pro Діми, чи заряд збігається із застосунком.
- Сумська область: графіки лише картинками в новинах; пропонувалось портувати OCR (fastiuk sumy_news). Не підтверджено.
- Social preview (`docs/social.png`) Діма завантажує вручну в Settings репозиторію.
- Області без графіків у меню: Сумська, Херсонська.

## Дизайн-правила
- Тексти в картці не мають обрізатися: довгі рядки — перенос з нерозривними пробілами (`Keep()`), перевіряти на 100–150% масштабу.
- Мінімум RAM/CPU: нічого важкого, поки картка закрита.
- Тема в меню називається просто «Темна» / «Світла».
