# MovieTheater Kubernetes Deployment with HTTPS

## 📋 Overview

This directory contains Kubernetes manifests for deploying the MovieTheater application to MicroK8s with **automated HTTPS/TLS** using Let's Encrypt via DNS-01 challenge through GoDaddy.

**Domain**: `theater.carpouzis.com`

---

## 📂 Files

### Core Kubernetes Manifests
- **`deployment.yaml`** - Application deployment (API + UI containers)
- **`service.yaml`** - NodePort service exposing the application
- **`ingress.yaml`** - Nginx ingress with TLS termination

### Certificate Management
- **`cert-issuer.yaml`** - Let's Encrypt production ClusterIssuer (DNS-01 via GoDaddy)
- **`cert-issuer-staging.yaml`** - Let's Encrypt staging ClusterIssuer (for testing)
- **`certificate.yaml`** - Standalone Certificate resource for theater.carpouzis.com

---

## 🔧 Architecture & How It Works

### The Challenge: Why DNS-01 Instead of HTTP-01?

**HTTP-01 was failing due to routing conflicts:**
- The main ingress would intercept ACME challenge requests meant for cert-manager's temporary solver ingress
- Reverse proxy issues caused Let's Encrypt to receive incorrect responses
- Timing issues: deploying the main ingress too early blocked challenges

**DNS-01 solves this by:**
- Bypassing HTTP routing entirely
- Proving domain ownership via DNS TXT records
- No dependency on ingress or reverse proxy configuration
- More reliable for complex routing scenarios

### Components

```
┌─────────────────────────────────────────────────────────────┐
│                     GitHub Actions Workflow                  │
│  (.github/workflows/movietheater-prod-deploy.yml)           │
└──────────────────┬──────────────────────────────────────────┘
                   │
                   ├──> 1. Build Docker images (API + UI)
                   │
                   ├──> 2. Setup cert-manager + GoDaddy webhook
                   │     └─> Helm chart: cert-manager-webhook-godaddy
                   │
                   ├──> 3. Deploy ClusterIssuer (cert-issuer.yaml)
                   │     └─> Configured with GoDaddy API credentials
                   │
                   ├──> 4. Deploy Application (deployment.yaml + service.yaml)
                   │
                   ├──> 5. Check existing certificate status
                   │     ├─> If ready: skip to step 7
                   │     └─> If not ready: continue to step 6
                   │
                   ├──> 6. Request Certificate (certificate.yaml)
                   │     └─> cert-manager uses GoDaddy to create DNS TXT record
                   │         └─> Let's Encrypt validates DNS
                   │             └─> Certificate issued to secret: movietheater-tls
                   │
                   └──> 7. Deploy Ingress (ingress.yaml)
                        └─> References movietheater-tls secret for TLS
```

### DNS-01 Challenge Flow

```
1. Certificate resource created
   └─> cert-manager detects new Certificate

2. cert-manager contacts Let's Encrypt
   └─> Let's Encrypt responds with DNS-01 challenge

3. cert-manager calls GoDaddy webhook
   └─> Webhook creates TXT record: _acme-challenge.theater.carpouzis.com

4. Let's Encrypt queries DNS for TXT record
   └─> DNS propagation (30-90 seconds)
       └─> Let's Encrypt validates domain ownership

5. Certificate issued
   └─> Stored in Kubernetes secret: movietheater-tls

6. Ingress uses certificate
   └─> HTTPS is live!
```

---

## 🚀 Deployment Process

### Prerequisites

**Required GitHub Secrets:**

| Secret Name | Description | Example |
|-------------|-------------|---------|
| `MOVIETHEATER_APPSETTINGS_JSON` | Production appsettings.json | `{"ConnectionStrings":...}` |
| `GODADDY_API_KEY` | GoDaddy API key | `dXXXXXXXXXXXX_XXXxxx...` |
| `GODADDY_API_SECRET` | GoDaddy API secret | `XXXxxxXXXxxx` |

**How to get GoDaddy credentials:**
1. Go to https://developer.godaddy.com/keys
2. Create new production API key
3. Save key + secret to GitHub Secrets

**DNS Configuration:**
```bash
# Ensure your domain points to the MicroK8s server
theater.carpouzis.com → <server-public-ip>
```

### Automated Deployment

**Simply push to master:**
```bash
git push origin master
```

The GitHub Actions workflow automatically:
1. ✅ Builds Docker images
2. ✅ Pushes to MicroK8s registry
3. ✅ Installs/configures cert-manager + GoDaddy webhook
4. ✅ Deploys ClusterIssuer with your GoDaddy credentials
5. ✅ Deploys application
6. ✅ Checks certificate status (skips issuance if already valid)
7. ✅ Requests certificate via DNS-01 (if needed)
8. ✅ Waits for certificate issuance (~90-120 seconds)
9. ✅ Deploys ingress with TLS
10. ✅ HTTPS is live at https://theater.carpouzis.com

**Expected timeline:**
- New deployment (no cert): ~3-5 minutes
- Re-deployment (cert exists): ~2 minutes

---

## 🐛 Troubleshooting

### Certificate Not Issuing

**Check certificate status:**
```bash
microk8s kubectl get certificate -n movietheater
microk8s kubectl describe certificate movietheater-tls -n movietheater
```

**Check DNS challenge:**
```bash
# View challenge status
microk8s kubectl get challenge -n movietheater -o wide

# Check challenge details
microk8s kubectl describe challenge <challenge-name> -n movietheater
```

**Check cert-manager logs:**
```bash
# Main controller
microk8s kubectl logs -n cert-manager -l app=cert-manager --tail=100

# GoDaddy webhook
microk8s kubectl logs -n cert-manager -l app.kubernetes.io/name=cert-manager-webhook-godaddy --tail=100
```

**Verify GoDaddy credentials:**
```bash
microk8s kubectl get secret godaddy-api-key -n cert-manager -o yaml
```

### Certificate Stuck or Failed

**Clean up and retry:**
```bash
# Delete certificate resources (will trigger re-issuance)
microk8s kubectl delete certificate movietheater-tls -n movietheater
microk8s kubectl delete certificaterequest --all -n movietheater
microk8s kubectl delete challenge --all -n movietheater
microk8s kubectl delete secret movietheater-tls -n movietheater

# Re-deploy (push to master or manually apply)
microk8s kubectl apply -f k8s/certificate.yaml
```

### Ingress Not Working

**Check ingress status:**
```bash
microk8s kubectl get ingress -n movietheater
microk8s kubectl describe ingress movietheater-ingress -n movietheater
```

**Verify TLS secret exists:**
```bash
microk8s kubectl get secret movietheater-tls -n movietheater
```

**Check nginx ingress logs:**
```bash
microk8s kubectl logs -n ingress -l app.kubernetes.io/name=ingress-nginx --tail=100
```

### GoDaddy Webhook Issues

**Check webhook pod:**
```bash
microk8s kubectl get pods -n cert-manager -l app.kubernetes.io/name=cert-manager-webhook-godaddy
microk8s kubectl logs -n cert-manager -l app.kubernetes.io/name=cert-manager-webhook-godaddy --tail=100
```

**Check webhook deployment:**
```bash
microk8s kubectl get deployment cert-manager-webhook-godaddy -n cert-manager
microk8s kubectl describe deployment cert-manager-webhook-godaddy -n cert-manager
```

**Verify APIService:**
```bash
microk8s kubectl get apiservice v1alpha1.acme.snowdrop.it
microk8s kubectl describe apiservice v1alpha1.acme.snowdrop.it
```

**Re-deploy webhook (no Helm required):**
```bash
microk8s kubectl delete -f k8s/godaddy-webhook.yaml
sleep 5
microk8s kubectl apply -f k8s/godaddy-webhook.yaml
```

---

## 📊 Key Differences from HTTP-01

| Aspect | HTTP-01 (Old) | DNS-01 (Current) |
|--------|---------------|------------------|
| **Challenge Method** | HTTP endpoint | DNS TXT record |
| **Routing Dependency** | Yes (ingress required) | No (DNS only) |
| **Reverse Proxy Issues** | Affected | Not affected |
| **Firewall Requirements** | Port 80 open | None |
| **DNS Provider** | Not needed | GoDaddy API required |
| **Reliability** | Poor (routing conflicts) | Excellent |
| **Issuance Time** | 30-60 seconds | 90-120 seconds |

---

## ⚠️ Common Pitfalls (Learned from 50+ Failed Attempts)

### 1. **Deploying Ingress Too Early**
- ❌ **Problem**: Main ingress intercepts ACME HTTP-01 challenges
- ✅ **Solution**: Use DNS-01 (no HTTP dependency) OR deploy ingress AFTER certificate is ready

### 2. **GoDaddy Webhook Not Ready**
- ❌ **Problem**: ClusterIssuer deployed before webhook APIService is available
- ✅ **Solution**: Wait for `v1alpha1.acme.snowdrop.it` APIService to be ready before deploying ClusterIssuer

### 3. **Re-requesting Certificates Too Frequently**
- ❌ **Problem**: Let's Encrypt rate limits (5 failures/hour, 50 certs/week per domain)
- ✅ **Solution**: Check certificate status before requesting new one (implemented in workflow)

### 4. **Wrong Challenge Type in Certificate**
- ❌ **Problem**: Certificate resource specifies HTTP-01, but ClusterIssuer only supports DNS-01
- ✅ **Solution**: Certificate resource should NOT specify challenge type (inherits from ClusterIssuer)

### 5. **Stale Certificate Resources**
- ❌ **Problem**: Failed certificate requests leave behind stuck CertificateRequest/Challenge objects
- ✅ **Solution**: Clean up all related resources before retrying (see troubleshooting section)

---

## 🎯 Production Checklist

Before going live:
- [ ] GoDaddy API credentials set in GitHub Secrets
- [ ] DNS points to correct server IP
- [ ] MicroK8s ingress addon enabled
- [ ] cert-manager installed (handled by workflow)
- [ ] GoDaddy webhook installed (handled by workflow)
- [ ] Push to master and monitor workflow
- [ ] Verify certificate issued: `microk8s kubectl get certificate -n movietheater`
- [ ] Test HTTPS: `curl -v https://theater.carpouzis.com`
- [ ] Check certificate expiration: `openssl s_client -connect theater.carpouzis.com:443 -servername theater.carpouzis.com < /dev/null 2>/dev/null | openssl x509 -noout -dates`

---

## 📝 Notes

- Certificates auto-renew ~30 days before expiration
- Let's Encrypt production rate limits: 50 certificates per domain per week
- Use staging ClusterIssuer (`cert-issuer-staging.yaml`) for testing
- Certificate issuance typically takes 90-120 seconds with DNS-01
- The workflow intelligently skips certificate requests if a valid certificate already exists

**No SSH required. No manual commands. No secrets to configure. Just push to master.**

## HTTPS Architecture

### How It Works

```
Browser (HTTPS) 
    ↓
NGINX Ingress (TLS termination, port 443)
    ↓
Service (HTTP, port 80)
    ↓
Pods (HTTP containers)
```

**Deployment Flow:**
1. GitHub Actions workflow runs on self-hosted runner
2. Workflow enables microk8s addons (cert-manager, ingress)
3. Domain and email injected from GitHub Secrets via `sed`
4. ClusterIssuer deployed with your email
5. Ingress deployed with your domain + TLS config
6. cert-manager detects Ingress, requests Let's Encrypt certificate
7. HTTP-01 challenge completes (requires port 80 accessible)
8. Certificate issued and stored in Kubernetes secret
9. NGINX serves HTTPS with valid certificate

**Certificate Lifecycle:**
- Initial issuance: ~2 minutes after first deploy
- Auto-renewal: 30 days before expiration (Let's Encrypt certs last 90 days)
- Zero manual intervention required

## Configuration Details

### Configured Values

| Value | Location | Purpose |
|-------|----------|---------|
| `testmanager@test.com` | `k8s/cert-issuer.yaml` | Let's Encrypt notification email |
| `testeddomain.com` | `k8s/ingress.yaml` | Your domain name |

These values are directly in the manifest files - no templating or secrets needed.

### Testing with Let's Encrypt Staging

To avoid rate limits during testing, modify the ingress annotation:

1. **Edit `k8s/ingress.yaml`**:
   ```yaml
   cert-manager.io/cluster-issuer: "letsencrypt-staging"
   ```

2. **Deploy staging issuer first**:
   ```bash
   microk8s kubectl apply -f k8s/cert-issuer-staging.yaml
   ```

3. **Push to master and verify** (browser will show certificate warning - this is expected for staging)

4. **Switch back to production**:
   ```yaml
   cert-manager.io/cluster-issuer: "letsencrypt-prod"
   ```

## Configuration Details

## Verification & Troubleshooting

### Check Certificate Status

```bash
# View certificate resource
microk8s kubectl get certificate -n movietheater

# Expected output:
# NAME               READY   SECRET             AGE
# movietheater-tls   True    movietheater-tls   5m

# Detailed certificate info
microk8s kubectl describe certificate movietheater-tls -n movietheater

# Check the actual secret
microk8s kubectl get secret movietheater-tls -n movietheater
```

### Test HTTPS

```bash
# Test from command line
curl -I https://movietheater.yourdomain.com

# Should return HTTP/2 200 without certificate errors
```

### Common Issues

**Certificate pending/not ready:**
```bash
# Check certificate request status
microk8s kubectl get certificaterequest -n movietheater
microk8s kubectl describe certificaterequest -n movietheater

# Check HTTP-01 challenge
microk8s kubectl get challenge -n movietheater
microk8s kubectl describe challenge -n movietheater

# Check cert-manager logs
microk8s kubectl logs -n cert-manager deployment/cert-manager --tail=50
```

**DNS not resolving:**
```bash
# Verify DNS points to your server
nslookup testeddomain.com

# Should return your server's public IP
```

**Ingress not routing:**
```bash
# Check ingress status
microk8s kubectl describe ingress movietheater-ingress -n movietheater

# Check ingress controller logs
microk8s kubectl logs -n ingress-nginx -l app.kubernetes.io/name=ingress-nginx --tail=50
```

**Port 80/443 not accessible:**
```bash
# Verify ports are open (on your server)
sudo netstat -tlnp | grep ':80\|:443'

# Check firewall rules
sudo ufw status
# Ensure ports 80 and 443 are allowed
```

### Certificate Renewal

Certificates automatically renew ~30 days before expiration. To force renewal:

```bash
# Delete the secret to trigger renewal
microk8s kubectl delete secret movietheater-tls -n movietheater

# Delete and recreate the certificate
microk8s kubectl delete certificate movietheater-tls -n movietheater
microk8s kubectl apply -n movietheater -f k8s/ingress.yaml

# Watch renewal process
microk8s kubectl get certificate -n movietheater -w
```

## Graceful Shutdown Configuration

The deployment has been configured to handle pod termination gracefully to prevent issues with "old replicas pending termination" during rolling updates.

### Key Configuration Details

1. **terminationGracePeriodSeconds: 30**
   - Gives the pod 30 seconds to gracefully shut down before being forcefully terminated
   - This allows time for in-flight requests to complete

2. **preStop Lifecycle Hook**
   - Executes a 5-second sleep when pod termination begins
   - Runs simultaneously with SIGTERM being sent to the application
   - Provides a buffer for load balancers and services to remove the pod from their endpoints
   - Prevents new connections from being established during shutdown

### How It Works

During a rolling update or pod termination:

1. Kubernetes marks the pod for termination
2. The pod is removed from service endpoints (stops receiving new traffic)
3. The `preStop` hook and SIGTERM signal are sent simultaneously
   - The `preStop` hook executes (5-second sleep)
   - The application receives SIGTERM and begins graceful shutdown
4. Kubernetes waits up to `terminationGracePeriodSeconds` (30 seconds)
5. If still running after grace period, SIGKILL is sent

This ensures that old replicas terminate cleanly without getting stuck in a pending state.

## Deployment Status

To check overall deployment status:

```bash
microk8s kubectl get all -n movietheater
microk8s kubectl get ingress -n movietheater
microk8s kubectl get certificate -n movietheater
microk8s kubectl describe deployment movietheater -n movietheater
```

## Rollback

If a deployment fails:

```bash
# View deployment history
microk8s kubectl rollout history deployment/movietheater -n movietheater

# Rollback to previous version
microk8s kubectl rollout undo deployment/movietheater -n movietheater

# Rollback to specific revision
microk8s kubectl rollout undo deployment/movietheater -n movietheater --to-revision=2
```
