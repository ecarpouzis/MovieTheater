
# Tar all .csproj files to preserve directory structure
# https://andrewlock.net/optimising-asp-net-core-apps-in-docker-avoiding-manually-copying-csproj-files/
find .. -name "*.csproj" -print0 | tar -cvf projectfiles.tar --null -T -

docker build \
  -t localhost:32000/movietheater-api:$GITHUB_SHA \
  -t localhost:32000/movietheater-api:latest \
  -f Dockerfile.api \
  --network host \
  ..

