# Test fixtures

| File | Origin | Contains |
|---|---|---|
| `wishlist.json` | **synthetic** | 157 items in the exact shape `IWishlistService/GetWishlist/v1` returns. Generated, not anyone's real wishlist. `appid 999001` is a stable marker the tests assert on. |
| `getitems.json` | captured from the live API | 9 real store items from `IStoreBrowseService/GetItems/v1`, chosen to cover every `coming_soon_display` value plus an Early Access exit and released games. Public Steam catalogue data — no account fields (no steamid, priorities or add-dates). |

`getitems.json` is deliberately a real response: the precision mapping is the trickiest
logic in the app, and hand-written JSON would only prove the parser agrees with whatever
shape the test author imagined. To refresh it:

```bash
curl -s "https://api.steampowered.com/IStoreBrowseService/GetItems/v1/?input_json=$(
  python -c "import urllib.parse,json;print(urllib.parse.quote(json.dumps({
    'ids':[{'appid':i} for i in [440,2828710,3982150,1846700,612400,1203620,2307350,1485920,1086940]],
    'context':{'language':'english','country_code':'US'},
    'data_request':{'include_release':True,'include_assets':True,'include_basic_info':True}}),safe=''))"
)" -o tests/fixtures/getitems.json
```

Note that refreshing it will change release dates as Steam updates them, so the
assertions in `SteamClientTests` and `ReleaseMapperTests` may need adjusting.
