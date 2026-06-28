const http=require('http'), https=require('https'), fs=require('fs'), path=require('path');
const COOKIE=process.env.COOKIE, BUILD=path.join(process.cwd(),'build'), UP='theater.carpouzis.com';
const mime={'.html':'text/html','.js':'text/javascript','.css':'text/css','.json':'application/json','.svg':'image/svg+xml','.png':'image/png','.ico':'image/x-icon','.woff2':'font/woff2','.woff':'font/woff'};
const isApi=u=>/^\/(API|odata|Image|ImageThumb|SeriesImage|MiscImage|BoardgameImage)\b/i.test(u);
http.createServer((req,res)=>{
  if(isApi(req.url)){
    const chunks=[]; req.on('data',c=>chunks.push(c)); req.on('end',()=>{
      const body=Buffer.concat(chunks);
      const headers={...req.headers, host:UP, cookie:'.AspNetCore.Cookies='+COOKIE};
      delete headers['accept-encoding'];
      const pr=https.request({hostname:UP,path:req.url,method:req.method,headers},pu=>{res.writeHead(pu.statusCode,pu.headers);pu.pipe(res);});
      pr.on('error',e=>{res.writeHead(502);res.end('proxy err '+e.message);});
      if(body.length)pr.write(body); pr.end();
    });
    return;
  }
  let fp=path.join(BUILD, req.url.split('?')[0]);
  if(!fs.existsSync(fp)||fs.statSync(fp).isDirectory()) fp=path.join(BUILD,'index.html');
  res.writeHead(200,{'content-type':mime[path.extname(fp)]||'application/octet-stream'});
  fs.createReadStream(fp).pipe(res);
}).listen(4599,()=>console.log('proxy on 4599'));
