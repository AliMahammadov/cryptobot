# 🚀 Oracle Cloud Free Tier VPS — 24/7 Fasiləsiz Quraşdırma Təlimatı

Bu sənəd **CryptoSense Clean Architecture v2** Telegram Botunun Oracle Cloud Free Tier VPS (və ya istənilən Ubuntu Linux VPS) üzərində **24/7 fasiləsiz** və **0 AZN xərclə** quraşdırılması üçün addım-addım təlimatdır.

---

## 📌 1-ci Addım: Oracle Cloud Hesabı Açmaq və Pulsuz Server Yaratmaq

1. [oracle.com/cloud/free](https://www.oracle.com/cloud/free/) səhifəsinə daxil olun və **"Start for free"** seçin.
2. Qeydiyyatdan keçin (Şəxsiyyət və debet/kredit kartı məlumatlarını daxil edin — kartdan sadəcə 1$ yoxlama üçün bloklanıb dərhal geri qaytarılacaq).
3. Giriş etdikdən sonra **Oracle Cloud Console** panelinə keçin:
   * **Compute** $\rightarrow$ **Instances** $\rightarrow$ **Create Instance** düyməsinə klikləyin.
   * **Ad:** `cryptosense-vps`
   * **Image and Shape:**
     * **Image:** `Ubuntu 24.04` (və ya `Ubuntu 22.04 LTS`)
     * **Shape:** `VM.Standard.E2.1.Micro` (Always Free-Eligible) və ya `VM.Standard.A1.Flex` (Ampere ARM - Always Free).
   * **Add SSH Keys:** **"Save private key"** düyməsinə basaraq `.key` faylını kompüterinizə yükləyin (Serverə qoşulmaq üçün vacibdir).
   * **Create** düyməsinə vurun. 1-2 dəqiqəyə server **Running (Yaşıl)** olacaq və sizə **Public IP Address** (Məs: `150.230.12.34`) veriləcək.

---

## 💻 2-ci Addım: Serverə Qoşulmaq (SSH)

Terminalı (PowerShell, PuTTY və ya Termius) açıb aşağıdakı əmrlə serverə qoşulun:

```bash
ssh -i /path/to/your-private-key.key ubuntu@SERVER_IP_UNVANINIZ
```

*(Telefonunuzdan idarə etmək istəsəniz, App Store / Play Store-dan **Termius** tətbiqini yükləyib eyni açarla serverə telefondan da qoşula bilərsiniz).*

---

## ⚡ 3-cü Addım: Layihəni Serverə Yükləmək və 1 Əmrlə Quraşdırmaq

Serverə daxil olduqdan sonra GitHub reponuzu klonlayın:

```bash
git clone https://github.com/AliMahammadov/cryptobot.git
cd cryptobot
```

Və avtomatlaşdırılmış quraşdırma skriptini işə salın:

```bash
chmod +x setup_oracle_vps.sh
./setup_oracle_vps.sh
```

Bu skript avtomatik olaraq:
1. Əməliyyat sistemini yeniləyəcək.
2. **Microsoft .NET 9 SDK** və SQLite kitabxanalarını quraşdıracaq.
3. Kodu **Release** rejimində yığacaq (`dotnet publish`).
4. Linux `systemd` servisini (`cryptosense.service`) yaradacaq.
5. Botu arxa planda **24/7 rejimdə** işə salacaq.

---

## 🛠️ Əsas İdarəetmə Əmrləri (VPS Daxilində)

* **Botun canlı işləmə vəziyyətini yoxlamaq:**
  ```bash
  sudo systemctl status cryptosense
  ```
* **Canlı logları (konsol çıxışlarını) oxumaq:**
  ```bash
  sudo journalctl -u cryptosense -f
  ```
* **Botu yenidən başlatmaq:**
  ```bash
  sudo systemctl restart cryptosense
  ```
* **Botu dayandırmaq:**
  ```bash
  sudo systemctl stop cryptosense
  ```

---

## 📱 İdarəetmə Artıq Tamamilə Telefonunuzdadır!

Quraşdırma bitdikdən sonra:
* Kompüterinizi söndürə bilərsiniz.
* Telegram botunuza telefonunuzdan `Ali 23031999Am` yazaraq daxil olun.
* Bütün istifadəçiləri yaratmaq, silmək, parolları dəyişmək, coin siyahısını tənzimləmək və siqnalları izləmək **24/7 telefonunuzdan** təmin olunacaq!
