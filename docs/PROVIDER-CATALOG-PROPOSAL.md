# Наблюдения за страницами DNS-провайдеров

Автоматически обнаруженные адреса требуют проверки человеком. Это **не** готовый список DNS для приложения.
Одна страница может описывать разные профили, bootstrap, IPv6 и примеры. Не переносите все найденные IP в один профиль.
Отказы сайтов не выключают провайдеров и не меняют последний каталог.

## wlrus

Источник: https://site.dns.wlrus.cfd/

Текущие настройки:
```json
{
  "ipv4": [
    "95.85.240.254",
    "138.124.254.134"
  ],
  "ipv6": [],
  "doh": "https://dns.wlrus.cfd/dns-query",
  "dot": "dns.wlrus.cfd"
}
```

Опубликованные на странице адреса (кандидаты для проверки):
```json
{
  "source": "https://site.dns.wlrus.cfd/",
  "ipv4": [],
  "ipv6": [],
  "doh": [
    "https://dns.wlrus.cfd/dns-query"
  ],
  "explicitDot": []
}
```

## webshift

Источник: https://dns.webshiftvpn.ru/

Текущие настройки:
```json
{
  "ipv4": [
    "31.31.207.68"
  ],
  "ipv6": [],
  "doh": "https://d.webshiftvpn.ru/dns-query",
  "dot": null
}
```

Опубликованные на странице адреса (кандидаты для проверки):
```json
{
  "source": "https://dns.webshiftvpn.ru/",
  "ipv4": [
    "31.31.207.68",
    "8.8.8.8"
  ],
  "ipv6": [],
  "doh": [
    "https://d.webshiftvpn.ru/dns-query`,Vd=`/webshift-dns.mobileconfig`,Hd=`https://t.me/webshiftvpn`,Ud=`https://app.webshiftvpn.ru`,Wd=`https://webshiftvpn.ru`;function"
  ],
  "explicitDot": []
}
```

После проверки внесите точные изменения в `config/providers.json` или `config/provider-checkers.json`.
Старые IP сохраните в `legacyIpv4`, ID/key не меняйте. Выполните `tools/Sync-ProviderAvailability.ps1`, тесты и просмотрите `config/catalog.json`.
Только утверждённый и опубликованный `config/catalog.json` загружается приложениями. Этот отчёт и observations.json клиенты не читают.
