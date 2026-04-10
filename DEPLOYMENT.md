# MovieTheater Deployment Guide

## 🎯 Current Status

The MovieTheater application deploys to MicroK8s with **automated HTTPS** via Let's Encrypt using **DNS-01 challenge through GoDaddy**.

---

## 📦 What's Deployed

- **Application**: .NET 8 API + Next.js UI
- **Infrastructure**: MicroK8s on self-hosted Linux runner
- **HTTPS**: Let's Encrypt certificates via cert-manager
- **DNS**: theater.carpouzis.com (managed by GoDaddy)
- **Deployment**: Automated via GitHub Actions on push to `master`

---

## 🚀 How to Deploy

### 1. Push to Master

```bash
git push origin master
```

The GitHub Actions workflow automatically:
1. Builds Docker images (API + UI)
2. Pushes to MicroK8s registry
3. Installs/configures cert-manager + GoDaddy webhook
4. Deploys application pods
5. Requests TLS certificate (if needed)
6. Deploys ingress with HTTPS

**Timeline**: 3-5 minutes for new deployment, 2 minutes for re-deployment

---

## 🔐 Required Secrets

Set these in **GitHub Settings → Secrets and variables → Actions**:

| Secret Name | Description | How to Get |
|-------------|-------------|------------|
| `MOVIETHEATER_APPSETTINGS_JSON` | Production app configuration | Your appsettings.json as JSON string |
| `GODADDY_API_KEY` | GoDaddy API key | https://developer.godaddy.com/keys |
| `GODADDY_API_SECRET` | GoDaddy API secret | Same page as API key |

**Important**: GoDaddy API credentials are only shown once. Save them immediately.

---

## 🏗️ Architecture

### Certificate Management (DNS-01 Challenge)

```
┌─────────────────────────────────────────────────────────┐
│                  GitHub Actions Push                     │
└───────────────────────┬─────────────────────────────────┘
                        │
                        ├─► Build & Push Docker Images
                        │
                        ├─► Install cert-manager + GoDaddy webhook
                        │
                        ├─► Deploy Application Pods
                        │
                        ├─► Check Certificate Status
                        │    ├─ If ready: Skip to ingress
                        │    └─ If not ready:
                        │        │
                        │        ├─► Deploy Certificate Resource
                        │        │
                        │        ├─► cert-manager Requests from Let's Encrypt
                        │        │
                        │        ├─► GoDaddy Webhook Creates DNS TXT Record
                        │        │    (_acme-challenge.theater.carpouzis.com)
                        │        │
                        │        ├─► Let's Encrypt Validates DNS
                        │        │    (queries public DNS, no HTTP needed)
                        │        │
                        │        └─► Certificate Issued → movietheater-tls secret
                        │
                        └─► Deploy Ingress (uses movietheater-tls)
                             │
                             └─► HTTPS Live at https://theater.carpouzis.com
```

### Why DNS-01 Instead of HTTP-01?

**Previous approach (HTTP-01) failed due to:**
- Reverse proxy routing conflicts
- Ingress couldn't route ACME challenges correctly
- Timing issues with ingress deployment

**DNS-01 advantages:**
- Completely bypasses HTTP routing
- No dependency on ingress or reverse proxy
- More reliable for complex networking scenarios
- Proves domain ownership via DNS records

**Trade-off:**
- Requires DNS provider API (GoDaddy)
- Slightly slower (~90-120 seconds vs 30-60 seconds for HTTP-01)

---

## 📊 Monitoring Deployments

### GitHub Actions

Watch the workflow: https://github.com/ecarpouzis/MovieTheater/actions

### Kubernetes Status

```bash
# Check deployment
microk8s kubectl get pods -n movietheater

# Check certificate
microk8s kubectl get certificate -n movietheater

# Check ingress
microk8s kubectl get ingress -n movietheater

# View logs
microk8s kubectl logs -n movietheater -l app=movietheater --tail=100
```

### Certificate Status

```bash
# Quick status
microk8s kubectl get certificate movietheater-tls -n movietheater

# Detailed status
microk8s kubectl describe certificate movietheater-tls -n movietheater

# Check TLS secret
microk8s kubectl get secret movietheater-tls -n movietheater
```

### Test HTTPS

```bash
# Test endpoint
curl -v https://theater.carpouzis.com

# Check certificate expiration
openssl s_client -connect theater.carpouzis.com:443 -servername theater.carpouzis.com < /dev/null 2>/dev/null | openssl x509 -noout -dates
```

---

## 🐛 Troubleshooting

### Deployment Failed

**Check GitHub Actions logs:**
- Go to https://github.com/ecarpouzis/MovieTheater/actions
- Click on the failed run
- Review step-by-step output

### Certificate Not Issuing

**Check certificate status:**
```bash
microk8s kubectl describe certificate movietheater-tls -n movietheater
```

Look for errors in the "Events" section.

**Common issues:**

1. **GoDaddy API credentials wrong**
   ```bash
   # Check secret exists
   microk8s kubectl get secret godaddy-api-key -n cert-manager
   
   # Re-create with correct credentials
   microk8s kubectl delete secret godaddy-api-key -n cert-manager
   # Push to master to trigger re-deployment with fresh secrets
   ```

2. **Webhook not running**
   ```bash
   # Check webhook pod
   microk8s kubectl get pods -n cert-manager -l app.kubernetes.io/name=cert-manager-webhook-godaddy
   
   # Check webhook logs
   microk8s kubectl logs -n cert-manager -l app.kubernetes.io/name=cert-manager-webhook-godaddy
   ```

3. **Challenge failed**
   ```bash
   # Check challenge status
   microk8s kubectl get challenge -n movietheater
   
   # Describe challenge
   microk8s kubectl describe challenge -n movietheater
   ```

**Check cert-manager logs:**
```bash
microk8s kubectl logs -n cert-manager -l app=cert-manager --tail=100 | grep -i "movietheater\|error"
```

### HTTPS Not Working

**Check ingress:**
```bash
microk8s kubectl describe ingress movietheater-ingress -n movietheater
```

**Check nginx logs:**
```bash
microk8s kubectl logs -n ingress -l app.kubernetes.io/name=ingress-nginx --tail=100
```

**Verify DNS:**
```bash
nslookup theater.carpouzis.com
```

### Application Not Responding

**Check pods:**
```bash
# Pod status
microk8s kubectl get pods -n movietheater

# Pod logs
microk8s kubectl logs -n movietheater -l app=movietheater --tail=100

# Describe pod
microk8s kubectl describe pod -n movietheater -l app=movietheater
```

---

## 🔄 Manual Operations

### Force Certificate Renewal

```bash
# Delete certificate (will trigger re-issuance)
microk8s kubectl delete certificate movietheater-tls -n movietheater

# Reapply
microk8s kubectl apply -f k8s/certificate.yaml

# Watch progress
microk8s kubectl get certificate -n movietheater -w
```

### Clean Up Stuck Certificate

```bash
# Delete all certificate-related resources
microk8s kubectl delete certificate movietheater-tls -n movietheater
microk8s kubectl delete certificaterequest --all -n movietheater
microk8s kubectl delete challenge --all -n movietheater
microk8s kubectl delete secret movietheater-tls -n movietheater

# Wait for cleanup
sleep 10

# Trigger new deployment
git commit --allow-empty -m "Trigger deployment"
git push origin master
```

### Restart Application

```bash
# Rolling restart
microk8s kubectl rollout restart deployment/movietheater -n movietheater

# Watch rollout
microk8s kubectl rollout status deployment/movietheater -n movietheater
```

---

## 📚 Documentation

- **k8s/README.md** - Complete Kubernetes deployment documentation
- **k8s/cert-issuer.yaml** - ClusterIssuer configuration (with inline docs)
- **k8s/certificate.yaml** - Certificate resource (with inline docs)
- **k8s/ingress.yaml** - Ingress configuration (with inline docs)
- **.github/workflows/movietheater-prod-deploy.yml** - Full workflow with comments

---

## ✅ Success Indicators

**Healthy deployment shows:**

```bash
# Certificate ready
$ microk8s kubectl get certificate -n movietheater
NAME               READY   SECRET             AGE
movietheater-tls   True    movietheater-tls   5m

# Pods running
$ microk8s kubectl get pods -n movietheater
NAME                           READY   STATUS    RESTARTS   AGE
movietheater-xxxxxxxxxx-xxxxx  2/2     Running   0          5m

# Ingress with TLS
$ microk8s kubectl get ingress -n movietheater
NAME                   CLASS   HOSTS                    ADDRESS       PORTS     AGE
movietheater-ingress   nginx   theater.carpouzis.com    10.x.x.x      80, 443   5m
```

**HTTPS test:**
```bash
$ curl -I https://theater.carpouzis.com
HTTP/2 200 
date: ...
content-type: text/html
...
```

---

## 🎓 Key Lessons Learned

### What Didn't Work (50+ Attempts)

1. **HTTP-01 challenge** - Failed due to reverse proxy routing
2. **Deploying ingress before certificate** - Intercepted ACME challenges
3. **Not checking certificate status** - Wasted Let's Encrypt rate limits
4. **Deploying ClusterIssuer before webhook ready** - Silent failures
5. **Bash redirection syntax** - `&>/dev/null` doesn't work reliably in GitHub Actions

### What Works Now

1. **DNS-01 challenge** - Bypasses all HTTP routing issues
2. **Smart certificate checking** - Skips issuance if valid cert exists
3. **Proper sequencing** - Webhook → ClusterIssuer → Certificate → Ingress
4. **Extensive logging** - Easy troubleshooting in GitHub Actions
5. **Explicit shell specification** - All bash steps use `shell: bash` with `> /dev/null 2>&1`

### Why This Is Reliable

- **No HTTP dependencies** - DNS-01 only needs GoDaddy API
- **Idempotent operations** - Can run workflow repeatedly without issues
- **Status checks** - Waits for each component to be ready
- **Clean error handling** - Clear error messages for troubleshooting
- **Portable shell syntax** - Works consistently across GitHub Actions runners

---

**For questions or issues, check the troubleshooting section or review GitHub Actions logs.**
