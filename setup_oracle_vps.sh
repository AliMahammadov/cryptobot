#!/bin/bash
# ==============================================================================
# CryptoSense Clean Architecture v2 - Oracle Cloud Free Tier VPS Setup Script
# ==============================================================================

set -e

echo "🚀 [1/5] Sistem yenilənir və zəruri paketlər quraşdırılır..."
sudo apt-get update -y
sudo apt-get install -y wget curl git build-essential sqlite3

echo "📦 [2/5] Microsoft .NET 9 SDK quraşdırılır..."
# Ubuntu üçün .NET 9 quraşdırması
if ! command -v dotnet &> /dev/null; then
    sudo apt-get install -y dotnet-sdk-9.0 || {
        echo "Default repo tapılmadı, Microsoft paketləri əlavə edilir..."
        wget https://packages.microsoft.com/config/ubuntu/$(lsb_release -rs)/packages-microsoft-prod.deb -O packages-microsoft-prod.deb
        sudo dpkg -i packages-microsoft-prod.deb
        rm packages-microsoft-prod.deb
        sudo apt-get update -y
        sudo apt-get install -y dotnet-sdk-9.0
    }
fi

echo "🏗️ [3/5] Layihə /var/www/cryptosense qovluğuna publish edilir..."
sudo mkdir -p /var/www/cryptosense
sudo chown -R $USER:$USER /var/www/cryptosense

dotnet publish -c Release -o /var/www/cryptosense

echo "⚙️ [4/5] Linux Systemd Daemon Xidməti (cryptosense.service) yaradılır..."
sudo bash -c "cat > /etc/systemd/system/cryptosense.service" << EOF
[Unit]
Description=CryptoSense Telegram Bot & Market Scanner (Clean Architecture v2)
After=network.target

[Service]
WorkingDirectory=/var/www/cryptosense
ExecStart=/usr/bin/dotnet /var/www/cryptosense/CryptoSense.dll
Restart=always
RestartSec=5
KillSignal=SIGINT
SyslogIdentifier=cryptosense
User=$USER
Environment=DOTNET_ENVIRONMENT=Production
Environment=ASPNETCORE_URLS=http://0.0.0.0:5083

[Install]
WantedBy=multi-user.target
EOF

echo "🔄 [5/5] Xidmət işə salınır və 24/7 avtomatik başlama rejiminə qoşulur..."
sudo systemctl daemon-reload
sudo systemctl enable cryptosense.service
sudo systemctl restart cryptosense.service

echo "=============================================================================="
echo "✅ TƏBRİKLƏR! CryptoSense Botu Oracle VPS-də 24/7 UĞURLA İŞƏ SALINDI!"
echo "=============================================================================="
echo "📊 Xidmətin vəziyyətini yoxlamaq üçün: sudo systemctl status cryptosense"
echo "📜 Canlı logları oxumaq üçün:         sudo journalctl -u cryptosense -f"
echo "🔄 Xidməti yenidən başlatmaq üçün:    sudo systemctl restart cryptosense"
echo "🛑 Xidməti dayandırmaq üçün:          sudo systemctl stop cryptosense"
echo "=============================================================================="
