// CommonJS entry point for Phusion Passenger (Plesk)
async function start() {
  await import('./server.js');
}
start();
