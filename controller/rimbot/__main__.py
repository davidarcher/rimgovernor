import argparse
import uvicorn


def main():
    parser = argparse.ArgumentParser(description='RimBot colony controller')
    parser.add_argument('--port',type=int,default=8787)
    parser.add_argument('--backend', choices=['rimapi', 'rimbridge'], default='rimapi')
    parser.add_argument('--fresh-game', action='store_true', help='Launch the isolated bridge fixture')
    parser.add_argument('--reload',action='store_true',help='Reload controller code during development')
    args = parser.parse_args()
    if args.fresh_game:
        import os
        os.environ['RIMBOT_BRIDGE_FRESH'] = '1'
    module = 'rimbot.bridge_server:create_app' if args.backend == 'rimbridge' else 'rimbot.server:create_app'
    uvicorn.run(module,factory=True,host='127.0.0.1',port=args.port,reload=args.reload)


if __name__ == '__main__':
    main()
