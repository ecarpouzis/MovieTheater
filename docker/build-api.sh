
REGISTRY=${REGISTRY:-registry.local:32500}
IMAGE_TAG=${IMAGE_TAG:-latest}

# Tar all .csproj files to preserve directory structure
# https://andrewlock.net/optimising-asp-net-core-apps-in-docker-avoiding-manually-copying-csproj-files/
find .. -name "*.csproj" -print0 | tar -cvf projectfiles.tar --null -T -

docker build \
  --cache-from $REGISTRY/movietheater-api:latest \
  --build-arg BUILDKIT_INLINE_CACHE=1 \
  -t $REGISTRY/movietheater-api:$IMAGE_TAG \
  -f Dockerfile.api \
  --network host \
  ..

docker tag $REGISTRY/movietheater-api:$IMAGE_TAG $REGISTRY/movietheater-api:latest
docker push $REGISTRY/movietheater-api:latest
