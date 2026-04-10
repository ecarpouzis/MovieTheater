# Kubernetes Deployment Configuration

## Overview

This directory contains Kubernetes manifests for deploying the MovieTheater application with **fully automated HTTPS/TLS** using Let's Encrypt.

## Files

- `deployment.yaml` - Main deployment configuration for the application
- `service.yaml` - Service configuration to expose the application
- `ingress.yaml` - Ingress configuration with TLS/HTTPS support (template)
- `cert-issuer.yaml` - Let's Encrypt production certificate issuer (template)
- `cert-issuer-staging.yaml` - Let's Encrypt staging certificate issuer (for testing)

## 🚀 Fully Automated Setup

### One-Time Configuration (GitHub Secrets)

**Set these secrets in your GitHub repository:**

1. Go to: **Settings → Secrets and variables → Actions → New repository secret**

2. **Add these secrets:**

   | Secret Name | Example Value | Description |
   |-------------|---------------|-------------|
   | `DOMAIN_NAME` | `movietheater.yourdomain.com` | Your domain name |
   | `LETSENCRYPT_EMAIL` | `admin@yourdomain.com` | Email for Let's Encrypt notifications |

3. **Ensure DNS points to your server:**
   ```bash
   nslookup movietheater.yourdomain.com
   # Should return your server's public IP address
   ```

4. **Push to master** - Everything else is automatic! 🎉

### What Happens Automatically

Every push to master:

1. ✅ Enables cert-manager addon (if not already enabled)
2. ✅ Enables ingress addon (if not already enabled)
3. ✅ Waits for cert-manager to be ready
4. ✅ Deploys cert-issuer with your email (testmanager@test.com)
5. ✅ Deploys application
6. ✅ Deploys ingress with TLS for testeddomain.com
7. ✅ Let's Encrypt automatically issues certificate (~2 min)
8. ✅ HTTPS is live with valid certificate!

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
