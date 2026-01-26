Starting off this fixes cars from showing up but sometimes you need to clear cache after saving in the tool. sometimes... you know how this game is. A useful command I used in the video in order to refresh the vehicle marketplace instantly was:

career_modules_vehicleShopping.invalidateVehicleCache()career_modules_vehicleShopping.updateVehicleList(true)

if you have any questions my discord is ic.ey just send me a friend request and ill help you, also im looking for more tools to make to make this game more enjoyable or addons for career mode.

If a vehicle still has a high price at that point its being based off of age + mileage and whatever dealer range / multiplier is along with part value. So if your config is setup at 150k and it shows millions then its one of those reasons its so expensive.

In this mod’s marketplace, Population is used as a weight when picking configs — it’s not a fixed count. Higher = more likely to appear when the dealership pool is generated.

There’s no hard scale, but here’s a practical guideline that keeps variety without spamming one config:

Ultra‑rare: 1–50
Rare: 50–200
Uncommon: 200–800
Common: 800–3,000
Very common: 3,000–10,000
Floods the list: 10,000+
If you set Population to 80,000, that config will dominate the pool and crowd out others. It basically means “always show this.”

If you want a “normal” everyday car that shows up often but doesn’t drown everything else, try 1,000–3,000. For a special trim or exotic, 50–300. For a junkyard-ish or low demand variant, 100–500.
