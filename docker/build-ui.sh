
REGISTRY=${REGISTRY:-registry.local:32500}
IMAGE_TAG=${IMAGE_TAG:-latest}
# The commit that ends up in index.html's mt-build marker. CI sets it from github.sha; a hand-run
# build falls back to this checkout's HEAD.
MT_BUILD=${MT_BUILD:-$(git rev-parse HEAD 2>/dev/null || echo unknown)}

docker build \
  --cache-from $REGISTRY/movietheater-ui:latest \
  --build-arg BUILDKIT_INLINE_CACHE=1 \
  --build-arg MT_BUILD="$MT_BUILD" \
  -t $REGISTRY/movietheater-ui:$IMAGE_TAG \
  -f Dockerfile.ui \
  --network host \
  ..

docker tag $REGISTRY/movietheater-ui:$IMAGE_TAG $REGISTRY/movietheater-ui:latest
docker push $REGISTRY/movietheater-ui:latest
