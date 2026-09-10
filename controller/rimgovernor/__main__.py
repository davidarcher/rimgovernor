import argparse
import uvicorn


def main():
    parser = argparse.ArgumentParser(description='RimGovernor colony controller')
    parser.add_argument('--host', choices=['127.0.0.1', '0.0.0.0'], default='127.0.0.1')
    parser.add_argument('--port',type=int,default=8787)
    parser.add_argument('--fresh-game', action='store_true', help='Launch the isolated bridge fixture')
    parser.add_argument('--reload',action='store_true',help='Reload controller code during development')
    parser.add_argument('--colonies', action='store_true', help='Serve only the local colony directory; do not start a game or controller')
    parser.add_argument('--resume', help='Resume a verified native-save/controller checkpoint in Manual')
    args = parser.parse_args()
    if args.colonies:
        if args.fresh_game or args.resume:
            parser.error('--colonies cannot be combined with --fresh-game or --resume')
        uvicorn.run('rimgovernor.local_colonies:create_directory_app', factory=True,
                    host=args.host, port=args.port, reload=args.reload)
        return
    if args.resume:
        if args.fresh_game or args.reload: parser.error('--resume cannot be combined with --fresh-game or --reload')
        import os
        from pathlib import Path
        from .session_checkpoint import prepare_resume
        checkpoint, state = prepare_resume(args.resume)
        os.environ.update(RIMGOVERNOR_BRIDGE_ROOT=checkpoint['root'], RIMGOVERNOR_DATA=str(state),
            RIMGOVERNOR_BRIDGE_FRESH='1' if checkpoint.get('owned', True) else '0', RIMGOVERNOR_HEADLESS='1' if checkpoint['headless'] else '0',
            RIMGOVERNOR_RESUME_CHECKPOINT=str(Path(args.resume).resolve()))
    if args.fresh_game:
        import os
        os.environ['RIMGOVERNOR_BRIDGE_FRESH'] = '1'
    module = 'rimgovernor.bridge_server:create_app'
    uvicorn.run(module,factory=True,host=args.host,port=args.port,reload=args.reload)


if __name__ == '__main__':
    main()
