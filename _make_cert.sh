#!/usr/bin/env bash
# 生成 Flux 稀疏包用的自签名证书。
#
# Subject 必须与 AppxManifest.xml 里的 <Identity Publisher> 完全一致，
# 否则 Add-AppxPackage 会拒绝安装。
#
# 密码从环境变量 FLUX_CERT_PASSWORD 读取，脚本里不写明文：
#   export FLUX_CERT_PASSWORD='你的密码'
#   bash _make_cert.sh
#
# 产物：
#   certs/Flux-Sparse.pfx  —— 私钥，**已被 .gitignore 排除，切勿提交**
#   certs/Flux-Sparse.cer  —— 公钥，随安装包分发（安装时导入「受信任的人」）

set -e

ROOT="$(cd "$(dirname "$0")" && pwd)"
CERT_DIR="$ROOT/certs"
mkdir -p "$CERT_DIR"

PFX="Flux-Sparse.pfx"
CER="Flux-Sparse.cer"
SUBJ="CN=Flux"

if [ -f "$CERT_DIR/$PFX" ]; then
  echo "证书已存在，跳过生成：$CERT_DIR/$PFX"
  exit 0
fi

if [ -z "$FLUX_CERT_PASSWORD" ]; then
  echo "错误：请先设置证书密码" >&2
  echo "  export FLUX_CERT_PASSWORD='你的密码'" >&2
  exit 1
fi

# 传给 PowerShell 的密码用 base64 转移，避免特殊字符（引号/$/反引号）破坏脚本
PW_B64=$(printf '%s' "$FLUX_CERT_PASSWORD" | base64 -w0)

powershell -NoProfile -ExecutionPolicy Bypass -Command "
\$ErrorActionPreference='Stop'
\$subj = '$SUBJ'
\$dir  = '$CERT_DIR'
\$pfx  = Join-Path \$dir '$PFX'
\$cer  = Join-Path \$dir '$CER'
\$pw   = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('$PW_B64'))
\$c = New-SelfSignedCertificate -Type Custom -Subject \$subj \`
     -KeyUsage DigitalSignature \`
     -FriendlyName 'Flux Sparse Package' \`
     -CertStoreLocation 'Cert:\\CurrentUser\\My' \`
     -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3','2.5.29.19={text}')
\$sec = ConvertTo-SecureString -String \$pw -Force -AsPlainText
Export-PfxCertificate -Cert \$c -FilePath \$pfx -Password \$sec | Out-Null
Export-Certificate   -Cert \$c -FilePath \$cer | Out-Null
Write-Output ('CERT_OK subject=' + \$c.Subject + ' thumbprint=' + \$c.Thumbprint)
Write-Output ('NotAfter=' + \$c.NotAfter)
"

ls -la "$CERT_DIR"
echo
echo "提醒：$PFX 含私钥，已被 .gitignore 排除，不要手动 git add -f。"
