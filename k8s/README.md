# Kubernetes Deployment Configuration

## Overview

This directory contains Kubernetes manifests for deploying the MovieTheater application.

## Files

- `deployment.yaml` - Main deployment configuration for the application
- `service.yaml` - Service configuration to expose the application

## Graceful Shutdown Configuration

The deployment has been configured to handle pod termination gracefully to prevent issues with "old replicas pending termination" during rolling updates.

### Key Configuration Details

1. **terminationGracePeriodSeconds: 30**
   - Gives the pod 30 seconds to gracefully shut down before being forcefully terminated
   - This allows time for in-flight requests to complete

2. **preStop Lifecycle Hook**
   - Executes a 5-second sleep before sending the SIGTERM signal
   - Provides a buffer for load balancers and services to remove the pod from their endpoints
   - Prevents new connections from being established during shutdown

### How It Works

During a rolling update or pod termination:

1. Kubernetes marks the pod for termination
2. The pod is removed from service endpoints (stops receiving new traffic)
3. The `preStop` hook executes (5-second sleep)
4. SIGTERM signal is sent to the application
5. Application begins graceful shutdown
6. Kubernetes waits up to `terminationGracePeriodSeconds` (30 seconds)
7. If still running after grace period, SIGKILL is sent

This ensures that old replicas terminate cleanly without getting stuck in a pending state.

## Deployment

To apply these configurations:

```bash
kubectl apply -f k8s/deployment.yaml
kubectl apply -f k8s/service.yaml
```

To check deployment status:

```bash
kubectl get deployments
kubectl get pods
kubectl describe deployment movietheater
```
