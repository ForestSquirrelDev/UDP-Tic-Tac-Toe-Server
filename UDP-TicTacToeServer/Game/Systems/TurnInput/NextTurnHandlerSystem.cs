﻿using System;
using System.Linq;
using Game.Components;
using Game.Entities;
using LiteNetLib;
using PoorMansECS.Entities;
using PoorMansECS.Systems;
using Server.Game.Components;
using Server.Game.Entities;
using Server.Game.Systems.Events;
using Server.Shared.Network;
using ServerShared.Shared.Network;

namespace Server.Game.Systems.TurnInput {
    public class NextTurnHandlerSystem : SystemBase, ISystemsEventListener {
        private OutgoingMessagesPipe _outgoingMessagesPipe;

        public NextTurnHandlerSystem(SystemsContext context) : base(context) { }

        public void InjectDependencies(OutgoingMessagesPipe outgoingMessagesPipe) {
            _outgoingMessagesPipe = outgoingMessagesPipe;
        }
        
        protected override void OnStart() {
            _context.EventBus.Subscribe<PlayerInputEvent>(this);
        }

        public void ReceiveEvent<T>(T systemEvent) where T : ISystemEvent {
            if (systemEvent is not PlayerInputEvent playerInput) {
                return;
            }
            var requestMessage = playerInput.RequestMessage;

            var room = _context.World.Entities.GetFirst<Room>();
            var nextTurn = room.GetComponent<NextTurnComponent>();
            var associatedPlayer = _context.World.Entities.GetFirst<Player>(
                p => p.GetComponent<AssociatedPeerComponent>().Peer.Id == playerInput.RequestMessage.AssociatedPeer.Id);
            var associatedPeer = associatedPlayer.GetComponent<AssociatedPeerComponent>().Peer;
            var playerSide = associatedPlayer.GetComponent<GameSideComponent>().GameSide;
            Console.WriteLine($"Player input side: {playerInput.GameSide}. Next turn side: {nextTurn.NextTurnSide}");
            if (playerSide != nextTurn.NextTurnSide) {
                var responseMessage = new InputResponseMessage(false, InputResponseMessage.Reason.WrongTurn);
                _outgoingMessagesPipe.SendResponse(associatedPeer, requestMessage, responseMessage);
                return;
            }

            var gameState = room.GetComponent<GameStateComponent>().State;
            if (gameState != GameStateComponent.GameState.Ongoing) {
                var responseMessage = new InputResponseMessage(false, gameState == GameStateComponent.GameState.Idle 
                    ? InputResponseMessage.Reason.GameNotStarted 
                    : InputResponseMessage.Reason.GameFinished);
                _outgoingMessagesPipe.SendResponse(associatedPeer, requestMessage, responseMessage);
                return;
            }

            var grid = _context.World.Entities.GetFirst<Grid>();
            var cellsComponent = grid.GetComponent<GridCellsComponent>();
            var hasCell = grid.GetComponent<GridCellsComponent>().TryGetCell(playerInput.CellRow, playerInput.CellColumn, out var cell);
            if (!hasCell) {
                var responseMessage = new InputResponseMessage(false, InputResponseMessage.Reason.OutOfBounds);
                _outgoingMessagesPipe.SendResponse(associatedPeer, requestMessage, responseMessage);
                return;
            }
            cell.SetOccupationInfo(playerInput.GameSide);
            
            UpdateGameState(cellsComponent, cell, room, playerInput.GameSide);
            BroadcastTurnFinish(playerInput, requestMessage, _context, _outgoingMessagesPipe);
        }

        private void UpdateGameState(GridCellsComponent cellsComponent, GridCell targetCell, IEntity room, GameSide gameSide) {
            room.SetComponent(new NextTurnComponent(gameSide == GameSide.Cross ? GameSide.Nought : GameSide.Cross));
            cellsComponent.SetCell(targetCell, targetCell.Row, targetCell.Column);
        }
        
        private void BroadcastTurnFinish(PlayerInputEvent playerInput, MessageWrapper requestMessage, SystemsContext context, OutgoingMessagesPipe outgoingMessagesPipe) {
            var inputResponse = new InputResponseMessage(true, InputResponseMessage.Reason.None);
            outgoingMessagesPipe.SendResponse(requestMessage.AssociatedPeer, requestMessage, inputResponse);
            var inputFinishedMessage = new TurnFinishedMessage(playerInput.CellRow, playerInput.CellColumn, (byte)playerInput.GameSide);
            var playerPeers = context.World.Entities.GetAll<Player>().Select(p => p.GetComponent<AssociatedPeerComponent>().Peer);
            outgoingMessagesPipe.SendToAllOneWay(playerPeers, inputFinishedMessage, DeliveryMethod.ReliableOrdered);
            context.EventBus.SendEvent(new TurnFinishedEvent(playerInput.GameSide));
        }

        protected override void OnStop() { }
        protected override void OnUpdate(float delta) { }
    }
}
