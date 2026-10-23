# Выпуск Android 1.5.1

Статус на 23 октября 2026 года: исходники и неподписанный release APK подготовлены. Новый GitHub Release и тег `android-v1.5.1` ещё не созданы. Выпуск передаётся разработчику, у которого есть прежний ключ подписи Android. Windows 2.23.2 уже опубликована отдельно.

## Что готово

- `app/build.gradle`: versionName `1.5.1`, versionCode `8`; applicationId `ru.kodvsego.smartdns`, minSdk 26, targetSdk 35.
- Общий каталог провайдеров, актуальный резервный адрес DNS-AI, сохранение номеров провайдеров и обработка отключённых вариантов.
- 87 JUnit/Robolectric-тестов прошли без пропусков и ошибок; Android Lint release — 0 замечаний.
- `testDebugUnitTest lintRelease assembleRelease` завершились успешно. Проверены метаданные и выравнивание release APK.
- Готовое [описание релиза](release-notes/1.5.1.md).

Локальные файлы подготовки находятся в игнорируемой папке `dist/android-1.5.1/`. Файл `SmartDNS-Android-1.5.1-unsigned.apk` **не подписан**: он не является установочным обновлением и не должен попадать в пользовательский релиз. Предпочтительный путь выпуска — собрать подписанный APK из этих исходников на машине с прежним ключом.

## 1. Прежний ключ подписи

На машине подготовки `kodvsego-android.p12` и параметры его подписи не найдены. Требуется исходный ключ либо его резервная копия и пароли. Создание нового ключа не позволит установить обновление поверх 1.5.0.

SHA-256 сертификата, подтверждённый проверкой APK из опубликованного релиза `android-v1.5.0`:

```text
6F:9D:E6:76:12:CE:DA:D4:04:63:93:E9:B9:48:54:53:68:96:DF:5D:0A:83:C6:F9:EE:63:C2:0E:B2:D9:6F:0E
```

SHA-256 самого опубликованного APK 1.5.0 (это другой отпечаток):

```text
f9b267905545e6abc2d1bde75684c46e78b8416dec743a57b8d7c2ab0407f98d
```

Восстановите `android/signing.properties` по `signing.properties.example`, указав прежний ключ, alias и пароли. Этот файл и сам ключ исключены из Git; не добавляйте их в коммиты или релиз. Не используйте отладочную подпись.

## 2. Сборка и проверка

Нужны JDK 17 или совместимый с Gradle 8.9 JDK, Android SDK Platform 35 и Build Tools 34.0.0+. В `ANDROID_HOME` либо `android/local.properties` должен быть задан путь SDK.

Из папки `android` на Windows:

```powershell
.\gradlew.bat testDebugUnitTest lintRelease assembleRelease --no-daemon
```

Linux/macOS: `bash gradlew testDebugUnitTest lintRelease assembleRelease --no-daemon`.

При настроенной подписи результат — `android/app/build/outputs/apk/release/app-release.apk`. Если получился только `app-release-unsigned.apk`, настройка подписи не применена: публикацию продолжать нельзя.

Проверьте результат инструментами Android SDK:

```text
apksigner verify --verbose --print-certs app-release.apk
aapt dump badging app-release.apk
zipalign -c -p 4 app-release.apk
```

Подпись должна проходить проверку, SHA-256 сертификата — совпадать с указанным выше. Метаданные: `ru.kodvsego.smartdns`, versionName `1.5.1`, versionCode `8`, minSdk `26`. Проверьте обновление поверх установленной 1.5.0 без удаления данных, запуск и отключение DNS на телефоне. Такие проверки на устройстве при подготовке не выполнялись; результат добавьте в описание релиза, если выполните их.

## 3. Файлы релиза

Из корня репозитория после успешной проверки подписанного APK:

```powershell
$releaseDir = 'dist/android-1.5.1/release'
New-Item -ItemType Directory -Path $releaseDir -Force | Out-Null
$apkName = 'KodVsego-SmartDNS-Android-1.5.1.apk'
$apkPath = Join-Path $releaseDir $apkName
Copy-Item -LiteralPath 'android/app/build/outputs/apk/release/app-release.apk' -Destination $apkPath
$hash = (Get-FileHash -Algorithm SHA256 -LiteralPath $apkPath).Hash.ToLowerInvariant()
[IO.File]::WriteAllText((Join-Path (Resolve-Path $releaseDir).Path 'SHA256SUMS.txt'), "$hash  $apkName`n", [Text.UTF8Encoding]::new($false))
```

Контрольную сумму пересчитывайте именно для окончательного подписанного файла. Сумма неподписанного APK не подходит.

## 4. Публикация на GitHub

Перед публикацией исходный коммит должен быть отправлен в `Kam300/KodVsego-SmartDNS`. В README пока сохранены рабочие ссылки на 1.5.0; после публикации замените их на 1.5.1 и удалите пометку «ещё не опубликована» в README и Android-документации.

Следующие команды выполняются из корня репозитория. Новый Android-релиз сохраняет предварительный статус; Windows остаётся основным latest-релизом:

```powershell
$commit = (git rev-parse HEAD).Trim()
gh release create android-v1.5.1 --repo Kam300/KodVsego-SmartDNS --target $commit --title 'Код Всего · Android 1.5.1 — каталог провайдеров и проверки DNS' --notes-file android/release-notes/1.5.1.md --prerelease --draft dist/android-1.5.1/release/KodVsego-SmartDNS-Android-1.5.1.apk dist/android-1.5.1/release/SHA256SUMS.txt
gh release view android-v1.5.1 --repo Kam300/KodVsego-SmartDNS --json isDraft,assets,targetCommitish
```

Убедитесь, что оба файла загружены полностью, размер APK соответствует локальному файлу, а `digest` APK равен `sha256:` плюс его локальный SHA-256. Черновик должен ссылаться на проверенный исходный коммит. Только после проверки:

```powershell
gh release edit android-v1.5.1 --repo Kam300/KodVsego-SmartDNS --draft=false --prerelease=true --latest=false
```

Ожидаемый адрес: `https://github.com/Kam300/KodVsego-SmartDNS/releases/tag/android-v1.5.1`. Сейчас он ещё не опубликован. Приложение 1.5.0 обнаруживает новые Android APK по версии в имени файла, учитывает предварительные релизы и проверяет SHA-256, пакет, versionCode и подпись перед установкой. Подробности — в [UPDATES.md](UPDATES.md).
