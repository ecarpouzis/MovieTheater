
docker build \
  -t localhost:32000/movietheater-ui:$GITHUB_SHA \
  -t localhost:32000/movietheater-ui:latest \
  -f Dockerfile.ui \
  --network host \
  ..

