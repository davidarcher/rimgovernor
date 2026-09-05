import argparse
import uvicorn


def main():
    parser = argparse.ArgumentParser(description='RimBot colony controller')
    parser.add_argument('--port',type=int,default=8787)
    parser.add_argument('--reload',action='store_true',help='Reload controller code during development')
    args = parser.parse_args()
    uvicorn.run('rimbot.server:create_app',factory=True,host='127.0.0.1',port=args.port,reload=args.reload)


if __name__ == '__main__':
    main()
