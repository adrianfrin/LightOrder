'use strict';
const http = require('node:http');
const fs = require('node:fs');
const path = require('node:path');
const root = path.resolve(__dirname, '../src/LightOrder.App/Assets');
const types = {'.html':'text/html; charset=utf-8','.js':'text/javascript; charset=utf-8','.css':'text/css; charset=utf-8','.svg':'image/svg+xml','.ico':'image/x-icon'};
http.createServer((request,response)=>{
  const pathname = new URL(request.url,'http://localhost').pathname;
  const file = pathname === '/' ? 'index.html' : pathname.slice(1);
  if (!/^[a-zA-Z0-9.-]+$/.test(file)) { response.writeHead(404); response.end(); return; }
  const target = path.join(root,file);
  fs.readFile(target,(error,data)=>{response.writeHead(error?404:200,{'Content-Type':types[path.extname(target)]||'application/octet-stream','Cache-Control':'no-store'});response.end(error?'Not found':data);});
}).listen(4312,'127.0.0.1',()=>console.log('Production assets preview: http://localhost:4312 (no native hardware bridge)'));
