#!/bin/bash
# ============================================
# 🐧 TradingSystem Linux VPS Deployment Script
# ============================================

set -e

echo "🚀 TradingSystem Linux Deployment"
echo "=================================="

# Configuration Variables
APP_NAME="tradingsystem"
APP_DIR="/var/tradingsystem"
SERVICE_USER="trading"
DOTNET_VERSION="8.0"

# 1. Install .NET Runtime
echo "📦 Installing .NET Runtime..."
if ! command -v dotnet &> /dev/null; then
    wget https://packages.microsoft.com/config/ubuntu/22.04/packages-microsoft-prod.deb -O packages-microsoft-prod.deb
    sudo dpkg -i packages-microsoft-prod.deb
    rm packages-microsoft-prod.deb
    sudo apt-get update
    sudo apt-get install -y aspnetcore-runtime-${DOTNET_VERSION}
fi
dotnet --info

# 2. Create Service User
echo "👤 Creating service user..."
if ! id "$SERVICE_USER" &>/dev/null; then
    sudo useradd -r -s /bin/false $SERVICE_USER
fi

# 3. Create Application Directory
echo "📁 Creating application directory..."
sudo mkdir -p $APP_DIR
sudo mkdir -p $APP_DIR/logs

# 4. Copy Application Files (Assuming already published to ./publish directory)
echo "📋 Copying application files..."
if [ -d "./publish" ]; then
    sudo cp -r ./publish/* $APP_DIR/
else
    echo "⚠️  Warning: ./publish directory not found"
    echo "   Run 'dotnet publish -c Release -r linux-x64' first"
fi

# 5. Set Permissions
echo "🔐 Setting permissions..."
sudo chown -R $SERVICE_USER:$SERVICE_USER $APP_DIR
sudo chmod +x $APP_DIR/TradingSystem.Console

# 6. Create systemd Service File
echo "⚙️  Creating systemd service..."
sudo tee /etc/systemd/system/${APP_NAME}.service > /dev/null << 'EOF'
[Unit]
Description=TradingSystem Crypto Trading Bot
After=network.target
StartLimitIntervalSec=0

[Service]
Type=simple
User=trading
WorkingDirectory=/var/tradingsystem
ExecStart=/var/tradingsystem/TradingSystem.Console
Restart=always
RestartSec=10

# Environment Variables (API Keys should be set via environment variables)
Environment=TRADING_APIKEY=your_api_key_here
Environment=TRADING_APISECRET=your_api_secret_here
Environment=TRADING_USETESTNET=true
Environment=DOTNET_ENVIRONMENT=Production

# Resource Limits
LimitNOFILE=65536
MemoryMax=1G

# Logs
StandardOutput=append:/var/tradingsystem/logs/stdout.log
StandardError=append:/var/tradingsystem/logs/stderr.log

[Install]
WantedBy=multi-user.target
EOF

# 7. Reload systemd
echo "🔄 Reloading systemd..."
sudo systemctl daemon-reload

# 8. Configure API Keys
echo ""
echo "⚠️  IMPORTANT: Configure your API keys!"
echo "   Edit: /etc/systemd/system/${APP_NAME}.service"
echo "   Set TRADING_APIKEY and TRADING_APISECRET"
echo ""

# 9. Enable and Start Service
echo "🎯 Service commands:"
echo "   Start:   sudo systemctl start ${APP_NAME}"
echo "   Stop:    sudo systemctl stop ${APP_NAME}"
echo "   Status:  sudo systemctl status ${APP_NAME}"
echo "   Logs:    sudo journalctl -u ${APP_NAME} -f"
echo "   Enable:  sudo systemctl enable ${APP_NAME}"
echo ""

# 10. Health Check
echo "🏥 Health check endpoint:"
echo "   curl http://localhost:8080/health"
echo "   curl http://localhost:8080/status"
echo ""

echo "✅ Deployment complete!"
echo ""
echo "📋 Next steps:"
echo "   1. Edit /etc/systemd/system/${APP_NAME}.service"
echo "   2. Set your Binance API keys"
echo "   3. Run: sudo systemctl daemon-reload"
echo "   4. Run: sudo systemctl enable ${APP_NAME}"
echo "   5. Run: sudo systemctl start ${APP_NAME}"
