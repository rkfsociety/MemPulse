# MemPulse

**MemPulse** — кроссплатформенный инструмент мониторинга и оптимизации оперативной памяти для Windows 10/11 и Linux (`x64`, `ARM64`). Интерфейс на русском языке, построен на **Avalonia 12** (единый UI в тёмной теме для Windows и Linux) и поддерживает работу как в автономном режиме (`MemPulse`), так и в качестве подключаемого модуля для хаба **PCPulse** (`MemPulse.PulseModule`).

[**Скачать последнюю версию**](https://github.com/rkfsociety/MemPulse/releases/latest)

## Возможности

- Загрузка ОЗУ в процентах, круговой индикатор, динамический график истории и метрики памяти.
- Группировка процессов по имени: поиск по имени или PID, сортировка, раскрытие отдельных PID, точечное сжатие памяти и белый список исключений.
- Единый однооконный интерфейс с вкладками **«Обзор»**, **«Процессы»**, **«Настройки»**, **«О программе»** (в тёмной теме).
- Работа интерфейса **без UAC и без root при старте**: привилегированные операции выполняются по запросу через отдельный консольный помощник `MemPulse.Helper` (`runas` на Windows, `pkexec` на Linux).
- Автономные функции: системный трей с динамическим процентом ОЗУ, автозапуск и глобальная клавиша `Ctrl+Alt+M` на Windows, проверка обновлений на GitHub.

## Архитектура

- `src/MemPulse.Core` (`net10.0`) — кроссплатформенная бизнес-логика, модели, настройки, платформенные сервисы Windows/Linux, валидация путей и отчётов помощника.
- `src/MemPulse.Helper` (`net10.0`) — отдельный консольный/headless помощник для привилегированных операций очистки памяти (`--clean-cache`, `--trim-process`, `--auto-clean`).
- `src/MemPulse.PulseModule` (`net10.0`) — подключаемый модуль `IPulseModule` (`PCPulse.Contracts 1.0.x`) + корневой `UserControl` на Avalonia 12 (`module.json` с `id: "mempulse"`).
- `src/MemPulse` (`net10.0`) — автономный графический клиент для Windows и Linux, оборачивающий `MemPulseModule` локальным `IPulseHostContext` (оверлей-диалоги, трей, автозапуск, обновления).

## Установка и использование

### Windows (автономная версия)

1. Скачайте **`MemPulse-Setup-x64.exe`** или портативный архив из последнего релиза (содержит `MemPulse.exe` и `MemPulse.Helper.exe`).
2. Запускайте приложение с обычными правами (`asInvoker`). Просмотр памяти и процессов не требует прав администратора.
3. При нажатии **«Очистить память»** или сжатии процесса запускается `MemPulse.Helper.exe` с запросом повышения прав.

### Linux (автономная версия)

1. Скачайте **`MemPulse-linux-x64.tar.gz`** или **`MemPulse-linux-arm64.tar.gz`**.
2. Распакуйте и запустите без root:
   ```bash
   mkdir -p MemPulse
   tar -xzf MemPulse-linux-x64.tar.gz -C MemPulse
   cd MemPulse
   chmod +x MemPulse MemPulse.Helper
   ./MemPulse
   ```
3. Для установки в меню приложений текущего пользователя выполните `bash install.sh` (устанавливается в `${XDG_DATA_HOME:-$HOME/.local/share}/MemPulse`).

### Подключение в PCPulse

Каждый релиз дополнительно публикует артефакты модуля для `win-x64`, `linux-x64` и `linux-arm64`:
- `mempulse-module-<version>.manifest.json`
- `mempulse-module-<version>-<rid>.zip`
- `mempulse-module-<version>-<rid>.zip.sha256`

## Сборка из исходников и тесты

1. Загрузите локальный пакет контрактов `PCPulse.Contracts 1.0.0` (из релиза `contracts-v1.0.0`):
   - Windows: `./scripts/fetch-contracts.ps1`
   - Linux/macOS: `bash scripts/fetch-contracts.sh`
2. Соберите и запустите тесты:
   ```bash
   dotnet build MemPulse.sln -c Release --warnaserror
   dotnet test MemPulse.sln -c Release --no-build
   ```

## Версионирование и релизы

Версия всех проектов задаётся централизованно в `src/Directory.Build.props` (`<Version>`). При повышении версии в `main` GitHub Actions собирает автономные пакеты, установщик Windows (`MemPulse-Setup-x64.exe`) и zip-архивы модуля для PCPulse, после чего публикует релиз.

## Диагностика

В автономном режиме локальные журналы пишутся в `%LOCALAPPDATA%\MemPulse\Logs` (Windows) или во временный каталог приложения.

**Лицензия:** [MIT](LICENSE). Проект вдохновлён [Mem Reduct](https://github.com/henrypp/memreduct), но реализован независимо.
