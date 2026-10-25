# Новые DNS · 25 октября 2026

Каталог Windows и Android: [config/providers.json](../config/providers.json). ID 0–6 сохранены; GeoHide RU/EU и Xbox DNS остаются отключёнными. «Авто · beta» перенесён в начало списка Windows без изменения ID провайдеров.

| ID | Провайдер | IPv4 / bootstrap | DoH | DoT |
| --- | --- | --- | --- | --- |
| 7 | [Zapret DNS](https://dns.zapret.moe/) | 83.217.211.149 | https://dns.zapret.moe/dns-query | dns.zapret.moe |
| 8 | [GeoHide US](https://geohide.ru/) | 192.255.159.240, 192.255.159.241, 192.3.233.253, 107.175.76.134 | https://us.geohide.ru/dns-query | us.geohide.ru |
| 9 | [Malw DNS](https://info.dns.malw.link/setup) | 179.254.166.171, 164.5.224.81 | https://dns.malw.link/dns-query | dns.malw.link |
| 10 | [WLrus DNS](https://site.dns.wlrus.cfd/) | 95.85.240.254, 138.124.254.134 | https://dns.wlrus.cfd/dns-query | dns.wlrus.cfd |
| 11 | Webshift | 31.31.207.68 | https://d.webshiftvpn.ru/dns-query | Не опубликован |
| 12 | [AdGuard DNS](https://adguard-dns.io/en/public-dns.html) | 94.140.14.14, 94.140.15.15 | https://dns.adguard-dns.com/dns-query | dns.adguard-dns.com |
| 13 | [Malw · CF](https://info.dns.malw.link/) | 162.159.36.20, 162.159.36.5 | https://5u35p8m9i7.cloudflare-gateway.com/dns-query | 5u35p8m9i7.cloudflare-gateway.com |

IPv6 из инструкций: Zapret — `2a03:afc0:9::235d`; Malw — `2a12:bec4:1bb0:27dd::2`, `2a13:82c0:0:d::3`; AdGuard — `2a10:50c0::ad1:ff`, `2a10:50c0::ad2:ff`. Они сохранены для обычного системного DNS. Шифрованные режимы Windows используют IPv4 bootstrap; поведение IPv6 этих режимов не менялось.

Адреса GeoHide US получены из DNS имени `us.geohide.ru`, сверены с [официальным US hosts-файлом](https://github.com/Internet-Helper/GeoHideDNS/blob/main/hosts/us/hosts), затем каждый адрес проверен с сертификатом `us.geohide.ru`. Для WLrus использованы адреса из [API его сайта](https://site.dns.wlrus.cfd/api/status). Bootstrap Webshift и Malw CF получены разрешением их собственных имён и проверены с исходным именем TLS.

Webshift и Malw CF доступны только с шифрованием: наличие DoH и bootstrap не означает открытой рекурсии на порту 53. `94.140.14.14` и `94.140.15.15` относятся к AdGuard и не используются для Webshift.

В Авто добавлены новые провайдеры с опубликованной поддержкой выбранного сервиса. У Webshift пока нет подтверждённого списка ИИ; AdGuard — фильтрующий DNS. Эти записи доступны вручную. У Malw заявлены Antigravity и NotebookLM; отдельный маршрут Antigravity доступен также через локальный DoH. Обычные NRPT-правила для шифрованного Malw CF не предлагаются.

Проверены ответы `example.com` через DoH на каждом новом IPv4 и через DoT на адресах с опубликованным DoT. Сертификаты проверялись. Проверка выполнена из текущей сети без изменения настроек ПК, входа в аккаунт или запроса к модели; это не гарантия доступности из другой сети.

Движок поддерживает обычный DNS, DoH и DoT. Публикуемые сайтами DoQ, DoH3, ссылки импорта и hosts-профили не добавлены как отдельные транспорты. В Android обновлён общий каталог: его существующий DNS VPN использует DoH, отдельный DoT-транспорт этим изменением не добавлен.
