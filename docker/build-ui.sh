
REGISTRY=${REGISTRY:-registry.local:32500}
IMAGE_TAG=${IMAGE_TAG:-latest}

docker build \
  --cache-from $REGISTRY/movietheater-ui:latest \
  --build-arg BUILDKIT_INLINE_CACHE=1 \
  -t $REGISTRY/movietheater-ui:$IMAGE_TAG \
  -f Dockerfile.ui \
  --network host \
  ..

docker tag $REGISTRY/movietheater-ui:$IMAGE_TAG $REGISTRY/movietheater-ui:latest
docker push $REGISTRY/movietheater-ui:latest
