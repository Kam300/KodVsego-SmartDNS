# Структура проекта

В корне находятся README, лицензии и `build.bat`. Результаты сборки и локальные архивы хранятся в `dist/` и не попадают в Git.

```text
src/windows/        исходники приложения Windows
  App/              главное окно, обновления, один экземпляр приложения
  Antigravity/      отдельные DNS-правила, восстановление окна, перезапуск
  Dns/              системный DNS, локальный резолвер, DoH/DoT и туннель
  Diagnostics/      проверки сети, провайдеров и диагностический журнал
  Platform/         работа с процессами и системными API
  Providers/        каталог и пользовательские DNS, настройки видимости
  Ui/               диалоги и элементы интерфейса
  Properties/       версия приложения и манифест Windows
  sources.rsp       общий список исходников для сборки и тестов
android/            приложение Android
config/             общий каталог DNS-провайдеров
res/                шрифты, значки, Wintun и лицензии ресурсов
installer/          шаблоны MSI и вспомогательные функции установщика
tests/              автоматические тесты и отдельные ручные smoke-проверки
tools/
  build/            сборка MSI и полного комплекта файлов
  dev/              превью интерфейса
  diagnostics/      ручная диагностика подключения
docs/               инструкции и описания выпусков
dist/               сборки, доказательства проверок и локальные архивы
```

Некоторые старые файлы содержат несколько связанных классов: перенос по папкам не разделяет их реализацию и не меняет пространства имён.

## Основные команды

Выполняются из корня репозитория:

```powershell
# Обычная сборка: результат dist/dev/KodVsego_SmartDNS.<версия>.exe
.\build.bat

# Тесты: результаты по умолчанию во временной папке Windows
powershell.exe -NoProfile -File .\tests\run.ps1

# MSI, портативный ZIP и EXE: dist/<версия>/release
powershell.exe -NoProfile -File .\tools\build\build-distribution.ps1

# Только MSI
powershell.exe -NoProfile -File .\tools\build\build-msi.ps1

# Превью без применения DNS: dist/preview.png
powershell.exe -NoProfile -File .\tools\dev\preview.ps1 -Inactive -VpnPreview Off -AgPreview Off
```

Проверенная сборка в `tools/Build-VerifiedWindows.ps1` требует компилятор Microsoft Roslyn, упаковка MSI — WiX 5.0.2. Подробности: [дистрибутивы](DISTRIBUTION.md).

## Добавление исходников

Новый C#-файл Windows помещается в подходящий раздел `src/windows/` и добавляется в `sources.rsp`. Этот список используют обычная и проверенная сборки, тесты и smoke-проверки. `tools/Read-WindowsSources.ps1` проверяет отсутствие пропусков и повторов. В response-файле используются обратные слеши для совместимости с системным компилятором Windows.

`src/windows/Providers/ProviderAvailability.cs` генерируется из `config/providers.json` скриптом `tools/Sync-ProviderAvailability.ps1`; вручную его не редактируют. Версия Windows задаётся в `src/windows/Properties/AssemblyInfo.cs`, имя обычного EXE — в `build.bat`.

Старые файлы, ранее лежавшие в корне локальной копии, сохранены в `dist/archive/root-files-<дата-время>/`. Рядом лежит `archive-index.json` со списком перенесённых файлов. Эта локальная папка не нужна для сборки и не включается в релиз.
