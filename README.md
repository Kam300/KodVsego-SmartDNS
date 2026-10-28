# Код Всего · Smart DNS

Переключатель Smart DNS для **компьютеров Windows и телефонов Android**. Выбор провайдера, обычный или зашифрованный DNS (DoH/DoT), тест доступности и значки сервисов: ChatGPT, Claude, Gemini, ElevenLabs и Spotify.

> [!NOTE]
> Актуальная стабильная Windows-версия — **[2.23.9](https://github.com/Kam300/KodVsego-SmartDNS/releases/tag/v2.23.9)**. Android 1.5.1 — предварительная версия. [Все выпуски](https://github.com/Kam300/KodVsego-SmartDNS/releases).

## Скачать

| Windows · MSI 2.23.9 | Android · APK 1.5.1 |
| :---: | :---: |
| <a href="https://github.com/Kam300/KodVsego-SmartDNS/releases/download/v2.23.9/KodVsego_SmartDNS.Setup.2.23.9.msi"><img src="docs/download-button-generated.png" width="260" alt="Скачать Smart DNS для Windows — MSI 2.23.9"></a> | <a href="https://github.com/Kam300/KodVsego-SmartDNS/releases/download/android-v1.5.1/KodVsego-SmartDNS-Android-1.5.1.apk"><img src="docs/download-button-generated.png" width="260" alt="Скачать Smart DNS для Android — APK 1.5.1"></a> |

| Платформа | Скачать | Требования |
| --- | --- | --- |
| Windows · установщик MSI 2.23.9 | [Скачать MSI](https://github.com/Kam300/KodVsego-SmartDNS/releases/download/v2.23.9/KodVsego_SmartDNS.Setup.2.23.9.msi) | Windows 10 2004+ / Windows 11, x64, .NET Framework 4.8 · все 3 способа подключения |
| Windows · портативный ZIP 2.23.9 | [Скачать ZIP](https://github.com/Kam300/KodVsego-SmartDNS/releases/download/v2.23.9/KodVsego_SmartDNS.Portable.2.23.9.zip) | Распаковать архив целиком и запустить · все 3 способа подключения (`wintun.dll` в комплекте) |
| Windows · отдельный EXE 2.23.9 | [Скачать EXE](https://github.com/Kam300/KodVsego-SmartDNS/releases/download/v2.23.9/KodVsego_SmartDNS.2.23.9.exe) | Один файл без драйверов: способы 1 и 2 сразу; для способа 3 положите `wintun.dll` рядом |
| Android · APK 1.5.1, предварительная версия | [Скачать APK](https://github.com/Kam300/KodVsego-SmartDNS/releases/download/android-v1.5.1/KodVsego-SmartDNS-Android-1.5.1.apk) | Android 8.0+, без root |

Основной установщик Windows — **MSI** с ярлыком и удалением через настройки Windows. **Портативный ZIP** работает без установки и уже содержит `wintun.dll` рядом с программой (доступны все 3 способа подключения). В отдельный файл **EXE** библиотека `wintun.dll` не встроена, чтобы избежать ложных срабатываний антивирусов: в нём сразу работают способы **1 («Через Windows»)** и **2 («Через приложение»)**, а при выборе экспериментального способа **3 («Gemini / NotebookLM · beta»)** программа предложит в один клик переключиться на способ 2 либо использовать ZIP / MSI (или положить официальный `wintun.dll` 0.14.1 x64 в папку с EXE). Перед установкой или обновлением закройте работающую копию Smart DNS. При удалении MSI восстанавливает DNS, DoH, IPv6 и правила Antigravity; обновление через MSI очистку не запускает. [Различия, сборка и удаление](docs/DISTRIBUTION.md) · [Как устроены MSI и очистка](docs/MSI.md).

[Канал «Код Всего» — @TotalC0de](https://t.me/TotalC0de) (в ссылке цифра **0**) · [Поддержка — @TotalCodeAdmin](https://t.me/TotalCodeAdmin) · [Сообщить об ошибке](https://github.com/Kam300/KodVsego-SmartDNS/issues)

> [!IMPORTANT]
> Smart DNS может помочь с доступом к сервисам, поддерживаемым выбранным оператором. **Успешный DNS-ответ не гарантирует открытие сайта, доступ к аккаунту, обход региональной ошибки или работу Gemini.** Набор сервисов зависит от провайдера и может меняться. IP-адрес подключения к сайту приложение не скрывает.

## Проверка VirusTotal — предыдущие версии

**Новые файлы Windows 2.23.9 ещё не проверены через VirusTotal.** Ниже приведены результаты прошлых выпусков; они не подтверждают результат новой сборки.

Проверено 2026-10-26 (Windows 2.23.8) и 2026-10-25 (Android 1.5.1). Ссылки ведут на полный отчёт VirusTotal; в знаменателе — движки, которые поддерживают этот тип файла.

| Платформа | Файл | Срабатывания / движков | Кто сработал | SHA-256 |
| --- | --- | :---: | --- | --- |
| Windows | KodVsego_SmartDNS.Setup.2.23.8.msi | [0 / 61](https://www.virustotal.com/gui/file/2d56fbae81e9b7e7cc9486525a4abf1723021434584260c095d26fbb4db347be/detection) | — | 2d56fbae81e9b7e7cc9486525a4abf1723021434584260c095d26fbb4db347be |
| Windows | KodVsego_SmartDNS.Portable.2.23.8.zip | [0 / 67](https://www.virustotal.com/gui/file/9d97b2f09352e7d59bbd215d7a10cdda38a1a93692c177e4ec717a0e7bd74461/detection) | — | 9d97b2f09352e7d59bbd215d7a10cdda38a1a93692c177e4ec717a0e7bd74461 |
| Windows | KodVsego_SmartDNS.2.23.8.exe | [1 / 69](https://www.virustotal.com/gui/file/c60d23caf85e866dc818bf83861ca3015b2b9fbadea6b0002c95020e62d0cd42/detection) | CrowdStrike | c60d23caf85e866dc818bf83861ca3015b2b9fbadea6b0002c95020e62d0cd42 |
| Android | KodVsego-SmartDNS-Android-1.5.1.apk | [0 / 66](https://www.virustotal.com/gui/file/9f45eecacd68844612383c5d68adda283e7ac1d27bedd471f1119df48ab5fcf6/detection) | — | 9f45eecacd68844612383c5d68adda283e7ac1d27bedd471f1119df48ab5fcf6 |

Сверяйте SHA-256 с файлом контрольных сумм нужного выпуска. Результат одного сервиса не гарантирует отсутствие обнаружений на конкретном ПК.

## Что нового в Windows 2.23.9

- Общий обновляемый каталог DNS: новые утверждённые адреса без переустановки приложения.
- Галочка автообновления, краткий статус и ручная проверка прямо в общих настройках.
- Работающее подключение сохраняется, свои DNS не заменяются; неверный каталог отклоняется.
- Актуальные адреса Comss.one и bootstrap для DoH/DoT.
- Наблюдение за официальными сайтами готовит PR для проверки, без автоматического распространения найденных адресов.

Исходники Android 1.5.2 подготовлены; APK с прежней подписью должен выпустить второй разработчик. Опубликованный APK пока остаётся 1.5.1. Авто и экспериментальные способы сохраняют отметку **beta**.

[Описание выпуска](docs/releases/2.23.9.md) · [Каталог DNS](docs/REMOTE-CATALOG.md) · [История выпусков](https://github.com/Kam300/KodVsego-SmartDNS/releases)

<img src="docs/ui-dark.png" width="280" alt="Тёмная тема Smart DNS"> <img src="docs/ui-light.png" width="280" alt="Светлая тема Smart DNS">

## Структура исходников

Код Windows находится в `src/windows/`, Android — в `android/`. Скрипты упаковки — в `tools/build/`, тесты — в `tests/`, результаты сборки — в `dist/`. [Карта проекта и команды](docs/PROJECT-STRUCTURE.md).

## На компьютере — Windows

1. Скачайте MSI из Releases и установите программу. Для запуска без установки выберите ZIP или отдельный EXE.
2. Выберите DNS-провайдера. В карточке показаны адреса и логотипы сервисов.
3. Выберите способ подключения и шифрование: через Windows, через приложение или экспериментальный профиль Gemini / NotebookLM; DoH · HTTPS либо DoT · TLS. Обычный DNS доступен через Windows. При необходимости настройте отдельный Antigravity.
4. Нажмите включение и подтвердите UAC. Затем полностью перезапустите браузер или нужное приложение.
5. Для восстановления исходных настроек выключите DNS в приложении.

Приложение сохраняет исходные настройки каждого адаптера до первой записи и поддерживает откат. Копии недоступных адаптеров сохраняются для ручного восстановления; автоматическая подстройка при смене сети не выполняется. Изменения выполняются штатными Windows API в короткоживущем процессе с повышением прав. Интерфейс работает с обычными правами. Есть светлая/тёмная тема, проверка сети/VPN, тест DNS, перезапуск браузера с профилями и сохраняемый диагностический отчёт.

Системный DoH требует поддерживаемой Windows 11. В этом режиме сохраняются параметры шифрования и привязки IPv6 для последующего восстановления. [Инструкция Windows и ограничения](docs/WINDOWS.md).

## На телефоне — Android

**Android 1.5.1 (предварительная версия):** обновлён DNS-AI, MagicDNS поддерживает обычный DNS и DoH с проверками Antigravity; GeoHide и GeoHide EU временно отключены. Провайдеры и домены проверок берутся из общего каталога. 88 тестов прошли, Android Lint release — без замечаний. APK подписан прежним ключом; пакет и схема настроек сохранены. Установка поверх 1.5.0 и смена сетей на реальном телефоне не проверены. [Описание релиза](https://github.com/Kam300/KodVsego-SmartDNS/releases/tag/android-v1.5.1) · [Проверки выпуска](android/RELEASE_1.5.1.md).

Начиная с Android 1.5.0 приложение автоматически проверяет GitHub при открытии или возвращении на экран (не чаще раза в час). Для нового APK появляется окно **«Доступно обновление» → «Обновить» / «Позже»**. Есть ручная кнопка «Проверить обновления». Загрузка начинается после согласия, установка подтверждается системным окном Android. [Как работают обновления и как выпускать APK](android/UPDATES.md).

**Первый переход с 1.4.1 и более старых версий на актуальный APK нужно установить вручную**: в старых APK этой функции ещё нет.

1. Скачайте `KodVsego-SmartDNS-Android-1.5.1.apk` из [Android-релиза](https://github.com/Kam300/KodVsego-SmartDNS/releases/tag/android-v1.5.1).
2. Откройте APK и разрешите установку из выбранного источника, если Android попросит.
3. Выберите провайдера, обычный DNS или DoH и нажмите «Включить DNS».
4. Подтвердите системный запрос VPN. Для постоянного отображения состояния разрешите уведомления.
5. Полностью перезапустите нужное приложение. Отключить DNS можно в приложении, уведомлении или виджете.
6. Если окно добавления не появляется, выберите «Как добавить вручную»: удерживайте пустое место рабочего стола → «Виджеты» → «Код Всего · Smart DNS».
7. Нажмите «Добавить виджет» и выберите компактный 2×1 или расширенный 4×2. Также можно добавить виджет «Код Всего» через меню рабочего стола.

Android-версия использует **локальный DNS-туннель через VpnService**. В него направляются только запросы к виртуальному DNS-адресу; трафик сайтов идёт напрямую через Wi-Fi или мобильную сеть. Есть обработка DNS по UDP и TCP, основной/резервный сервер для обычного DNS, DoH через HTTPS с HTTP/2, тест каждого сервера и два виджета рабочего стола. Значки встроены в APK, тема и выбор сохраняются на телефоне.

Особенности Android:

- Другой VPN одновременно использовать нельзя: Android предоставляет один VPN-слот на профиль. Перед заменой приложение показывает подтверждение.
- Строгий «Частный DNS» Android нужно вручную переключить на «Автоматически» или «Выкл.». Настройки Android приложение не переписывает.
- Собственный Secure DNS браузера и встроенные DNS-клиенты приложений могут обходить настройку.
- DoH не переходит на обычный DNS при ошибке HTTPS. Это защита DNS-запросов, а не шифрование всего интернет-трафика. Имя самого DoH endpoint определяется через физическую сеть.
- IPv6-трафик сайтов остаётся прямым; A и AAAA запросы обслуживаются через локальный IPv4 DNS. Принудительного отключения IPv6 нет.
- Windows NRPT, реестр, UAC и принудительный перезапуск чужих приложений на Android не используются.
- Always-on, запуск при загрузке и режим «Блокировать соединения без VPN» не поддерживаются. После остановки туннеля Android возвращает DNS физической сети.

**Статус Android 1.5.1:** предварительная версия. Ядро, возврат из фона, клики, остановка и действия виджетов проверяются JUnit/Robolectric (Android 9 и 15), сборка — Android Lint. Подключённого телефона/эмулятора при разработке не было: системный диалог VPN, смена Wi-Fi/мобильной сети и реальный TUN на устройстве ещё требуют проверки. Это не подтверждение работоспособности на всех телефонах. [Устройство Android-версии и сборка](android/README.md).

## Провайдеры

| Провайдер | IPv4 / подключение | DoH |
| --- | --- | --- |
| [Comss.one](https://www.comss.ru/page.php?id=7315) | `83.220.169.155`, `212.109.195.93` | `https://dns.comss.one/dns-query` |
| [Bezmezhau](https://bezmezhau.com/ru/setup) | `181.215.40.49`, `31.58.216.201` | `https://dns.bezmezhau.com/dns-query` |
| [DNS-AI](https://dns-ai.ru/) | `194.87.196.13`, `45.151.102.219` · только с шифрованием | `https://dns.dns-ai.ru/dns-query` |
| [MagicDNS](https://magicdns.ru/) | `109.120.132.43`, `94.249.177.102` | `https://dns.magicdns.ru/dns-query` |
| [Zapret DNS](https://dns.zapret.moe/) | `83.217.211.149` | `https://dns.zapret.moe/dns-query` |
| [GeoHide US](https://geohide.ru/) | `192.255.159.240`, `192.255.159.241`, `192.3.233.253`, `107.175.76.134` | `https://us.geohide.ru/dns-query` |
| [Malw DNS](https://info.dns.malw.link/setup) | `179.254.166.171`, `164.5.224.81` | `https://dns.malw.link/dns-query` |
| [WLrus DNS](https://site.dns.wlrus.cfd/) | `95.85.240.254`, `138.124.254.134` | `https://dns.wlrus.cfd/dns-query` |
| [Webshift](https://dns.webshiftvpn.ru/) | `31.31.207.68` · только с шифрованием | `https://d.webshiftvpn.ru/dns-query` |
| [AdGuard DNS](https://adguard-dns.io/ru/public-dns.html) | `94.140.14.14`, `94.140.15.15` | `https://dns.adguard-dns.com/dns-query` |
| [Malw · CF](https://info.dns.malw.link/setup/cloudflare) | `162.159.36.20`, `162.159.36.5` · только с шифрованием | `https://5u35p8m9i7.cloudflare-gateway.com/dns-query` |

Источник параметров — общий [каталог провайдеров](config/providers.json). GeoHide RU/EU и Xbox отключены; их прежние адреса сохраняются для восстановления. GeoHide US доступен как отдельный провайдер. Для системного DoH Comss используется отдельный адрес `195.133.25.16`.

В способе «Через приложение» основной DNS и отдельный Antigravity могут использовать разных провайдеров. Строка чекера показывает подтверждение только по поддерживаемому механизму сайта провайдера. Общий чекер Malw не различает обычный Malw и вариант Cloudflare. Доступность сайта, вход и ответ модели — разные проверки. [Механизмы и ограничения](docs/RESOLVER-CHECK.md).

## Сборка из исходников

### Windows

```bat
build.bat
```

Результат из текущих исходников: `dist/dev/KodVsego_SmartDNS.2.23.9.exe`. Используется системный компилятор .NET Framework; зависимости не скачиваются. Готовый релиз собирается через `tools/build/build-distribution.ps1` с проверенным Microsoft Roslyn; [состав пакетов и проверки](docs/DISTRIBUTION.md).

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tests\run.ps1
```

Отдельные проверки поиска DLL: `tests/ProcessSecurity.Tests.cs`. Превью интерфейса без переключения DNS: `tools/dev/preview.ps1`.

Проверка реальных переходов окна и отключения кадрового таймера: `powershell -NoProfile -ExecutionPolicy Bypass -File tests\UiMotion.Tests.ps1`. Скрипт не вызывает изменение DNS; окно находится за пределами экрана.

### Android

Установите JDK 17 и Android SDK Platform 35 / Build Tools 34.0.0. Создайте `android/local.properties` с `sdk.dir=путь/к/Android/Sdk` или задайте `ANDROID_HOME`.

```powershell
cd android
.\gradlew.bat testDebugUnitTest lintDebug assembleDebug
```

Linux/macOS: `bash gradlew testDebugUnitTest lintDebug assembleDebug`. APK для разработки находится в `android/app/build/outputs/apk/debug/app-debug.apk`. Для подписанного выпуска настройте собственный ключ по [инструкции](android/README.md#подпись-apk). Ключи и пароли не хранятся в Git.

[GitHub Actions](.github/workflows/build.yml) собирает Windows EXE и Android debug APK, запускает тесты и Lint. Сборочные файлы публикуются как артефакты CI; пользовательские подписанные APK — в Releases.

## Структура

- `XboxDns.cs`, `StyledDialog.cs` — Windows UI и логотипы сервисов.
- `NativeDns.cs`, `EncryptedDns.cs`, `DnsElevation.cs` — системные DNS Windows, резервные копии, DoH и UAC.
- `AntigravityDns.cs`, `AgTakeover.cs`, `AgCleanup.cs` — правила Antigravity на Windows.
- `android/app/src/main/java/ru/kodvsego/smartdns/` — Android UI, VPN-служба, DNS и HTTPS-транспорт.
- `res/power.svg` и Android `ic_power.xml` — векторный значок питания.
- `tests/` и `android/app/src/test/` — проверки платформ.
- `res/service-icons/` — общие логотипы Windows/Android и сведения об источниках.
- `docs/WINDOWS.md` — актуальная инструкция Windows и ограничения.

## Конфиденциальность и лицензия

Изменения в текущей версии: © 2026 «Код Всего». Исходное уведомление HB AI и условия MIT сохранены в [LICENSE](LICENSE); лицензии шрифтов — в `res/OFL-Unbounded.txt` и `res/OFL-Manrope.txt`.

Android-приложение не сохраняет историю DNS-запросов, не читает аккаунты и файлы других приложений и не отправляет диагностические отчёты автоматически. DNS-запросы получает выбранный DNS-оператор. У Windows-версии диагностический TXT создаётся по запросу пользователя; автоматической отправки нет.

Проект основан на [hb-ai-xbox-dns](https://github.com/neiroseti-ai/hb-ai-xbox-dns/) и распространяется по [MIT](LICENSE), [русский текст](LICENSE.ru.md). Исходные уведомления сохранены. Логотипы принадлежат соответствующим правообладателям, [источники](res/service-icons/README.md). Шрифты Windows — SIL OFL 1.1. Android-зависимости — [Apache 2.0](android/app/src/main/assets/third-party/NOTICE.txt).
