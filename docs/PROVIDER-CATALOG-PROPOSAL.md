# Наблюдения за страницами DNS-провайдеров

Автоматически обнаруженные адреса требуют проверки человеком. Это **не** готовый список DNS для приложения.
Одна страница может описывать разные профили, bootstrap, IPv6 и примеры. Не переносите все найденные IP в один профиль.
Отказы сайтов не выключают провайдеров и не меняют последний каталог.

## comss

Источник: https://www.comss.ru/page.php?id=7315

Текущие настройки:
```json
{
  "ipv4": [
    "94.250.253.55",
    "212.109.195.93"
  ],
  "ipv6": [],
  "doh": "https://dns.comss.one/dns-query",
  "dot": "dns.comss.one"
}
```

Опубликованные на странице адреса (кандидаты для проверки):
```json
{
  "source": "https://www.comss.ru/page.php?id=7315",
  "ipv4": [
    "195.133.25.16",
    "212.109.195.93",
    "212.109.221.20"
  ],
  "ipv6": [],
  "doh": [
    "https://dns.comss.one/dns-query"
  ],
  "explicitDot": [
    "dns.comss.one"
  ]
}
```

После проверки внесите точные изменения в `config/providers.json` или `config/provider-checkers.json`.
Старые IP сохраните в `legacyIpv4`, ID/key не меняйте. Выполните `tools/Sync-ProviderAvailability.ps1`, тесты и просмотрите `config/catalog.json`.
Только утверждённый и опубликованный `config/catalog.json` загружается приложениями. Этот отчёт и observations.json клиенты не читают.
