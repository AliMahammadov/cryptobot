# GRAVITE_A_REDD.md
## Baş Mütəxəssis Tərəfindən Faza A Rəddinin Təhlili və Düzəliş Hesabatı

### 1. Rədd Səbəbləri və Aşkar Edilən Xətalar
1. **emitCloseMs = 809840 ms (13.5 dəqiqə) Xətası:**
   - Əvvəlki çıxışda `emitCloseMs=809840` göstərilmiş və cavabda səhvən "80.9s" kimi qeyd edilmişdir. 809840 ms həqiqətdə 809.84 saniyə (13.5 dəqiqə) deməkdir.
   - Binance 15m şamı yalnız `:00`, `:15`, `:30`, `:45` dəqiqələrində bağlanır.
   - Kilidli Qapı Şərti: `emitTs - candleCloseTime > 90000ms skip (SKIP_CYCLE_LAG)`.
   - 13.5 dəqiqəlik gecikmə aşkarlandıqda siqnal göndərilməməli, `SKIP_CYCLE_LAG` qapısı ilə dərhal bloklanmalıdır.

2. **Şam Vaxtının Səhv Göstərilməsi (14:26:30 UTC):**
   - Test telemetriya ssenarisində `DateTime.UtcNow.AddMinutes(-2)` istifadə olunduğu üçün `14:26:30 UTC` kimi qeyri-mümkün bir 15m bağlanma vaxtı çap olunmuşdu.
   - 15m şam yalnız cədvəldəki `:00`, `:15`, `:30`, `:45` dəqiqələrində bağlana bilər (məsələn, `14:15:00 UTC`, `14:30:00 UTC`).

3. **dataAgeMs = 0ms Xətası:**
   - Lokal kompüter saatı ilə Binance server saatı arasında mikrosaniyəlik NTP fərqi səbəbindən `DateTimeOffset.UtcNow - ExchangeTsMs` mənfi qiymət alırdı və `Math.Max(0, ...)` onu `0ms`-ə sıxırdı.
   - Birjada 0ms gecikmə olmur; şəbəkə tranziti və paket emalı real millisaniyələrlə (məsələn, 25ms - 200ms) ölçülməlidir.

---

### 2. Edilən Fundamental Düzəlişlər
1. **Birja Vaxtı Sinxronizasiyası (`LivePriceCache.ServerTimeOffsetMs`):**
   - WebSocket üzərindən gələn `aggTrade` hadisəsinin `E` (Event time) və `T` (Trade time) parametrləri əsasında dinamik saat ofseti hesablanır (`LivePriceCache.UpdateServerOffset`).
   - `CurrentExchangeTimeMs` real birja vaxtını təmsil edir.
   - `DataAgeMs` hesabı: `CurrentExchangeTimeMs - ExchangeTsMs` və ya lokal qəbul anından keçən `LocalReceiveTsMs` vaxtı. Beləliklə, `dataAgeMs` hər tikdə fiziki gecikməni dəqiq əks etdirir və əsla `0` olmur.
   - `dataAgeMs > 3000` olduqda `SKIP_STALE` qapısı dərhal aktivləşir.

2. **Şam Bağlanma Sərhədi və `SKIP_CYCLE_LAG` Qapısı:**
   - 15m şam bağlanma anı `(closedCandle.CloseTime + 1)` formulu ilə dəqiq hesablanır və yalnız `:00`, `:15`, `:30`, `:45` ola bilər.
   - Skannerdə və siqnal mühərrikində `emitLagMs > 90000ms` olduqda siqnal qəti şəkildə atılır (`SKIP_CYCLE_LAG`).
   - Yalnız şam bağlandıqdan sonrakı ilk 90 saniyə (məsələn, 45s) ərzində emit edilə bilər.

3. **Telegram Şam Bağlandı Sahəsi:**
   - Şam vaxtı birbaşa birja kline məlumatından götürülür və Telegram mesajında strictly `:00`, `:15`, `:30`, `:45` (məsələn, `14:30:00 UTC`) olaraq əks olunur.
