import os, sys
from PIL import Image, ImageFilter
img = Image.open(sys.argv[1])
img.save(sys.argv[1])