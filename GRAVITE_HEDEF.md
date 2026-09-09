# GRAVITE_HEDEF.md
## Hədəf və Bağlanış Arxitekturasının Əsaslı Düzəliş Hesabatı

### 1. Üç Sətirlik Mahiyyət
1. **TP3 uydurma:** Klaster yoxdursa TP3 sahəsi silinir, $0 yazılmır, hit yoxlanmır. `1.2*atrPct` ilə və ya sintetik addımlarla TP3 uydurmaq qəti qadağandır.
2. **Şərt yalnız:** `TP1` 0.60–2.50%, `R:R ≥ 0.8`, `TP2 ≠ TP1` (yoxdursa TP2 də yox).
3. **BE yalnız SL çəkir, close yox:** MFE tetiki vurduqda Stop Loss girişə (+0.12% komissiya buferi ilə) çəkilir, mövqe açıq qalır. Çıxış yalnız canlı bazar qiymətinin (`snap.Last`) stopa toxunması ilə baş verir. 2 saniyəlik saxta "TP3" və spam bildirişlər tamamilə ləğv edildi.

---

### 2. Aşkar Edilən Hadisələr və Kök Səbəblər

| Siqnal | Baş Verən Hadisə | Kök Səbəb | Tətbiq Edilən Həll |
| :--- | :--- | :--- | :--- |
| **ARB SHORT** | ~10 dəq ərzində +0.9% MFE oldu; BE bütün treydi vaxtından əvvəl bağladı; eyni hesabat 4 dəfə spamləndi. | 1. Mövqe açılanda qeydə alınan tarixi `SessionHigh` (1.000) saxlanılırdı. BE stopu 0.9988-ə çəkdikdə `SessionHigh >= StopLoss` (1.000 >= 0.9988) şərti keçmiş qiymətlə dərhal ödənirdi.<br>2. WebSocket tikləri ardıcıl gəldikdə unthrottled `Task.Run` paralel çağırılırdı. | 1. BE və ya TP1 aktiv olduqda çıxış yalnız **canlı qiymət** (`snap.Last`) ilə yoxlanılır. Mövqe AÇIQ qalır.<br>2. `SemaphoreSlim(1,1)`, `_sentOutcomeDeduplication` və 250ms tik filtri ilə Telegram çıxış bildirişi ciddi şəkildə təkrarolunmaz (0 spam) edildi. |
| **APT LONG** | 2 saniyə içində “TP3 qazanc” elan olundu, lakin real PnL −0.02% idi. | S/R klasteri olmadıqda `TakeProfit3 = 0m` qalırdı. `snap.SessionHigh >= 0` şərti ilk canlı tikdə dərhal ödəndiyi üçün siqnal dərhal bağlandı. Əvvəlki düzəlişdə isə klaster olmayanda `1.2*atrPct` ilə sintetik TP3 uydurulurdu. | Klaster yoxdursa TP3 sahəsi silinir, 0 yazılmır, `snap.SessionHigh >= sig.TakeProfit3` yoxlanışı yalnız `TakeProfit3 > 0` olduqda işləyir. Sintetik ATR fallback tam silindi. |
| **TRX LONG** | 2 saniyəyə bağlandı; TP1 cəmi +0.20% (girişə yapışıq) idi və yenə TP3=0 idi. | Aşağı volatillikdə (ATR = 0.30%) `0.6 * atrPct` = 0.18% hesablanırdı və TP1 giriş qiymətinə həddindən artıq yaxın qoyulurdu. | Minimum TP1 məsafəsi `Math.Max(0.60m, 0.6m * atrPct)` ilə aşağıdan 0.60%-ə bağlandı. Skannerdə `SKIP_TP_NEAR` (< 0.60%) və `SKIP_TP_FAR` (> 2.50%) qapısı əlavə edildi. |
| **Bazar Statistikası** | 100% Win-Rate amma −200% PnL göstərirdi. | Mənfi nəticə ilə bitən əməliyyatlar (məsələn, APT −0.02%) TP3 bağlandı deyə `SignalStatus.Success` sayılırdı. | Bütün SQL və LINQ performans sorğularında `ResultPercent < 0` olan hər bir əməliyyat dərhal `Failed` kimi təsnif edildi. Tarixi saxta qeydlər bazada avtomatik `Failed`-ə çevrildi. |

---

### 3. Tətbiq Edilən Riyazi və Məntiqi İnvariantlar

#### 1. Hədəflərin (TP1, TP2, TP3) Təyini Qaydası
- **TP1 İntervalı:**
  $$\text{minDist} = \max(0.60\%, 0.60 \times \text{ATR}\%), \quad \text{maxDist} = 2.50\%$$
  $$\text{distPct} = \operatorname{clamp}(\text{clusterDist}, \text{minDist}, \text{maxDist})$$
  TP1 15m qrafikində heç vaxt 0.60%-dən yaxın və 2.50%-dən uzaq ola bilməz.
- **Risk:Mükafat (R:R) Qapısı:**
  $$R:R = \frac{|\text{TP1} - \text{Entry}|}{|\text{Entry} - \text{SL}|} \ge 0.80$$
  $R:R < 0.80$ olan bütün siqnallar `SKIP_RR` qapısı ilə bloklanır.
- **TP2 və TP3 Qaydası:**
  Yalnız və yalnız qrafikdə real növbəti klasterlər olduqda təyin edilir:
  - $\text{TP2}$: Yalnız 2-ci klaster mövcuddursa və $\text{TP2} \neq \text{TP1}$. Əks halda $\text{TP2} = 0$ (Telegram-da göstərilmir, çıxış gözlənilmir).
  - $\text{TP3}$: Yalnız $\text{TP2} > 0$ və 3-cü klaster mövcuddursa və $\text{TP3} \neq \text{TP2}$. Əks halda $\text{TP3} = 0$ (Telegram-da göstərilmir, $0 yazılmır, hit yoxlanmır).
  - `1.2 * atrPct` və ya sintetik addımlarla TP3 uydurmaq qətiyyən yoxdur.

#### 2. Qorunmuş Breakeven (BE) Mexanizmi
- **Tetik Şərti:**
  $$\text{MFE} \ge \max(0.55 \times \text{RiskRPct}, 0.70 \times \text{ATR}\%)$$
- **Stopun Çəkilməsi:**
  - LONG: $\text{StopLoss} = \text{Entry} \times (1 + 0.0012)$
  - SHORT: $\text{StopLoss} = \text{Entry} \times (1 - 0.0012)$
- **Mövqenin Saxlanması:**
  Stop çəkildikdə treyd bağlanmır (`IsClosed = false`, `RemainingPositionRatio = 1.0`).
- **Çıxış Şərti:**
  Tarixi `SessionHigh`/`SessionLow` deyil, yalnız cari birja tik qiyməti:
  - LONG: $\text{snap.Last} \le \text{StopLoss}$
  - SHORT: $\text{snap.Last} \ge \text{StopLoss}$

#### 3. Bildiriş İntizamı və Təkrarolunmazlıq (Anti-Spam)
- Hər siqnal üçün fərdi `SemaphoreSlim(1,1)` kilidi təmin edildi.
- `_sentOutcomeDeduplication` vasitəsilə hər siqnal hər mərhələ (TP1, TP2, TP3, BE, SL, TRAIL) üçün ən çoxu **1 dəfə** Telegram bildirişi göndərə bilər.

---

### 4. Test Doğrulama Nəticələri

Test mühitində 46 testin hamısı uğurla başa çatmışdır:
- **Test 40:** Signal Quality - 15m Max 90m Expiry, TP1 Distance Cap & BTC Compass Guard `[PASS]`
- **Test 42:** Risk:Reward Gate - R:R < 0.8 Strictly Blocked & Format Verified `[PASS]`
- **Test 45:** Target Integrity - TP1 >= 0.60%, TP1 <= 2.50%, TP2 != TP1, TP3 != TP2 `[PASS]`
- **Test 46:** Breakeven Invariant - MFE +0.9% pulls SL but DOES NOT close position `[PASS]`

```
========================================================
🏁 TEST NƏTİCƏLƏRİ: 46 UĞURLU (PASS), 0 UĞURSUZ (FAIL)
========================================================
```
