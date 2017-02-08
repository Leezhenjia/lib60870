/*
 *  Copyright 2016 MZ Automation GmbH
 *
 *  This file is part of lib60870.NET
 *
 *  lib60870.NET is free software: you can redistribute it and/or modify
 *  it under the terms of the GNU General Public License as published by
 *  the Free Software Foundation, either version 3 of the License, or
 *  (at your option) any later version.
 *
 *  lib60870.NET is distributed in the hope that it will be useful,
 *  but WITHOUT ANY WARRANTY; without even the implied warranty of
 *  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
 *  GNU General Public License for more details.
 *
 *  You should have received a copy of the GNU General Public License
 *  along with lib60870.NET.  If not, see <http://www.gnu.org/licenses/>.
 *
 *  See COPYING file for the complete license text.
 */

using System;

using lib60870;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace lib60870
{
	public class ServerConnection 
	{

		static byte[] STARTDT_CON_MSG = new byte[] { 0x68, 0x04, 0x0b, 0x00, 0x00, 0x00 };

		static byte[] STOPDT_CON_MSG = new byte[] { 0x68, 0x04, 0x23, 0x00, 0x00, 0x00 };

		static byte[] TESTFR_CON_MSG = new byte[] { 0x68, 0x04, 0x83, 0x00, 0x00, 0x00 };

		static byte[] TESTFR_ACT_MSG = new byte[] { 0x68, 0x04, 0x43, 0x00, 0x00, 0x00 };

		private int sendCount = 0;
		private int receiveCount = 0;

		private int unconfirmedMessages = 0; /* number of unconfirmed messages received */
		private Int64 lastConfirmationTime = System.Int64.MaxValue; /* timestamp when the last confirmation message was sent */

		/* T3 parameter handling */
		private UInt64 nextT3Timeout;
		private int outStandingTestFRConMessages = 0;

		private ConnectionParameters parameters;
		private Server server;

		public ServerConnection(Socket socket, ConnectionParameters parameters, Server server) 
		{
			this.socket = socket;
			this.parameters = parameters;
			this.server = server;

			Thread workerThread = new Thread(HandleConnection);

			workerThread.Start ();
		}

		/// <summary>
		/// Gets the connection parameters.
		/// </summary>
		/// <returns>The connection parameters used by the server.</returns>
		public ConnectionParameters GetConnectionParameters()
		{
			return parameters;
		}

		private void ResetT3Timeout() {
			nextT3Timeout = (UInt64) SystemUtils.currentTimeMillis () + (UInt64) (parameters.T3 * 1000);
		}

		/// <summary>
		/// Flag indicating that this connection is the active connection.
		/// The active connection is the only connection that is answering
		/// application layer requests and sends cyclic, and spontaneous messages.
		/// </summary>
		private bool isActive = false;

		/// <summary>
		/// Gets or sets a value indicating whether this connection is active.
		/// The active connection is the only connection that is answering
		/// application layer requests and sends cyclic, and spontaneous messages.
		/// </summary>
		/// <value><c>true</c> if this instance is active; otherwise, <c>false</c>.</value>
		public bool IsActive {
			get {
				return this.isActive;
			}
			set {
				isActive = value;

				if (debugOutput) {
					if (isActive)
						Console.WriteLine ("SLAVE: " + this.ToString () + " is active");
					else
						Console.WriteLine ("SLAVE: " + this.ToString () + " is not active");
				}
			}
		}

		private Socket socket;

		private bool running = false;

		private bool debugOutput = true;

		private int receiveMessage(Socket socket, byte[] buffer) 
		{
			if (socket.Poll (50, SelectMode.SelectRead)) {

				if (socket.Available == 0)
					throw new SocketException ();

				// wait for first byte
				if (socket.Receive (buffer, 0, 1, SocketFlags.None) != 1)
					return 0;

				if (buffer [0] != 0x68) {
					if (debugOutput)
						Console.WriteLine ("SLAVE: Missing SOF indicator!");
					return 0;
				}

				// read length byte
				if (socket.Receive (buffer, 1, 1, SocketFlags.None) != 1)
					return 0;

				int length = buffer [1];

				// read remaining frame
				if (socket.Receive (buffer, 2, length, SocketFlags.None) != length) {
					if (debugOutput)
						Console.WriteLine ("SLAVE: Failed to read complete frame!");
					return 0;
				}

				return length + 2;
			} else
				return 0;
		}

		private void sendIMessage(Frame frame) 
		{
			frame.PrepareToSend (sendCount, receiveCount);
			socket.Send (frame.GetBuffer (), frame.GetMsgSize (), SocketFlags.None);
			sendCount++;
		}

		private void sendSMessage() 
		{
			if (debugOutput)
				Console.WriteLine ("SLAVE: Send S message");

			byte[] msg = new byte[6];

			msg [0] = 0x68;
			msg [1] = 0x04;
			msg [2] = 0x01;
			msg [3] = 0;
			msg [4] = (byte) ((receiveCount % 128) * 2);
			msg [5] = (byte) (receiveCount / 128);

			socket.Send (msg);
		}

		public void SendASDU(ASDU asdu) {
			Frame frame = new T104Frame ();
			asdu.Encode (frame, parameters);

			sendIMessage (frame);
		}

		public void SendACT_CON(ASDU asdu, bool negative) {
			asdu.Cot = CauseOfTransmission.ACTIVATION_CON;
			asdu.IsNegative = negative;

			SendASDU (asdu);
		}

		public void SendACT_TERM(ASDU asdu) {
			asdu.Cot = CauseOfTransmission.ACTIVATION_TERMINATION;
			asdu.IsNegative = false;

			SendASDU (asdu);
		}

		private void IncreaseReceivedMessageCounters ()
		{
			receiveCount++;
			unconfirmedMessages++;

			if (unconfirmedMessages == 1) {
				// start timeout if only one unconfirmed message
				lastConfirmationTime = SystemUtils.currentTimeMillis();
			}
		}

		private bool HandleMessage(Socket socket, byte[] buffer, int msgSize)
		{
			ResetT3Timeout ();

			if ((buffer [2] & 1) == 0) {

				if (debugOutput)
					Console.WriteLine ("SLAVE: Received I frame");

				if (msgSize < 7) {

					if (debugOutput)
						Console.WriteLine ("SLAVE: I msg too small!");

					return false;
				}
					

				IncreaseReceivedMessageCounters ();

				if (isActive) {

					bool messageHandled = false;

					ASDU asdu = new ASDU (parameters, buffer, msgSize);

					switch (asdu.TypeId) {

					case TypeID.C_IC_NA_1: /* 100 - interrogation command */

						if (debugOutput)
							Console.WriteLine ("SLAVE: Rcvd interrogation command C_IC_NA_1");

						if ((asdu.Cot == CauseOfTransmission.ACTIVATION) || (asdu.Cot == CauseOfTransmission.DEACTIVATION)) {
							if (server.interrogationHandler != null) {

								InterrogationCommand irc = (InterrogationCommand)asdu.GetElement (0);

								if (server.interrogationHandler (server.InterrogationHandlerParameter, this, asdu, irc.QOI))
									messageHandled = true;
							}
						} else {
							asdu.Cot = CauseOfTransmission.UNKNOWN_CAUSE_OF_TRANSMISSION;
							this.SendASDU (asdu);
						}

						break;

					case TypeID.C_CI_NA_1: /* 101 - counter interrogation command */

						if (debugOutput)
							Console.WriteLine ("SLAVE: Rcvd counter interrogation command C_CI_NA_1");

						if ((asdu.Cot == CauseOfTransmission.ACTIVATION) || (asdu.Cot == CauseOfTransmission.DEACTIVATION)) {
							if (server.counterInterrogationHandler != null) {

								CounterInterrogationCommand cic = (CounterInterrogationCommand)asdu.GetElement (0);

								if (server.counterInterrogationHandler (server.counterInterrogationHandlerParameter, this, asdu, cic.QCC))
									messageHandled = true;
							}
						} else {
							asdu.Cot = CauseOfTransmission.UNKNOWN_CAUSE_OF_TRANSMISSION;
							this.SendASDU (asdu);
						}

						break;

					case TypeID.C_RD_NA_1: /* 102 - read command */

						if (debugOutput)
							Console.WriteLine ("SLAVE: Rcvd read command C_RD_NA_1");

						if (asdu.Cot == CauseOfTransmission.REQUEST) {

							if (debugOutput)
								Console.WriteLine ("SLAVE: Read request for object: " + asdu.Ca);

							if (server.readHandler != null) {
								ReadCommand rc = (ReadCommand)asdu.GetElement (0);

								if (server.readHandler (server.readHandlerParameter, this, asdu, rc.ObjectAddress))
									messageHandled = true;

							}

						} else {
							asdu.Cot = CauseOfTransmission.UNKNOWN_CAUSE_OF_TRANSMISSION;
							this.SendASDU (asdu);
						}

						break;

					case TypeID.C_CS_NA_1: /* 103 - Clock synchronization command */

						if (debugOutput)
							Console.WriteLine ("SLAVE: Rcvd clock sync command C_CS_NA_1");

						if (asdu.Cot == CauseOfTransmission.ACTIVATION) {

							if (server.clockSynchronizationHandler != null) {

								ClockSynchronizationCommand csc = (ClockSynchronizationCommand)asdu.GetElement (0);

								if (server.clockSynchronizationHandler (server.clockSynchronizationHandlerParameter,
									    this, asdu, csc.NewTime))
									messageHandled = true;
							}

						} else {
							asdu.Cot = CauseOfTransmission.UNKNOWN_CAUSE_OF_TRANSMISSION;
							this.SendASDU (asdu);
						}

						break;

					case TypeID.C_TS_NA_1: /* 104 - test command */

						if (debugOutput)
							Console.WriteLine ("SLAVE: Rcvd test command C_TS_NA_1");

						if (asdu.Cot != CauseOfTransmission.ACTIVATION)
							asdu.Cot = CauseOfTransmission.UNKNOWN_CAUSE_OF_TRANSMISSION;
						else
							asdu.Cot = CauseOfTransmission.ACTIVATION_CON;

						this.SendASDU (asdu);

						messageHandled = true;

						break;

					case TypeID.C_RP_NA_1: /* 105 - Reset process command */

						if (debugOutput)
							Console.WriteLine ("SLAVE: Rcvd reset process command C_RP_NA_1");

						if (asdu.Cot == CauseOfTransmission.ACTIVATION) {

							if (server.resetProcessHandler != null) {

								ResetProcessCommand rpc = (ResetProcessCommand)asdu.GetElement (0);

								if (server.resetProcessHandler (server.resetProcessHandlerParameter,
									this, asdu, rpc.QRP))
									messageHandled = true;
							}

						} else {
							asdu.Cot = CauseOfTransmission.UNKNOWN_CAUSE_OF_TRANSMISSION;
							this.SendASDU (asdu);
						}


						break;

					case TypeID.C_CD_NA_1: /* 106 - Delay acquisition command */

						if (debugOutput)
							Console.WriteLine ("SLAVE: Rcvd delay acquisition command C_CD_NA_1");

						if ((asdu.Cot == CauseOfTransmission.ACTIVATION) || (asdu.Cot == CauseOfTransmission.SPONTANEOUS)) {
							if (server.delayAcquisitionHandler != null) {

								DelayAcquisitionCommand dac = (DelayAcquisitionCommand)asdu.GetElement (0);

								if (server.delayAcquisitionHandler (server.delayAcquisitionHandlerParameter,
									    this, asdu, dac.Delay))
									messageHandled = true;
							}
						} else {
							asdu.Cot = CauseOfTransmission.UNKNOWN_CAUSE_OF_TRANSMISSION;
							this.SendASDU (asdu);
						}

						break;

					}

					if ((messageHandled == false) && (server.asduHandler != null))
						if (server.asduHandler (server.asduHandlerParameter, this, asdu))
							messageHandled = true;

					if (messageHandled == false) {
						asdu.Cot = CauseOfTransmission.UNKNOWN_TYPE_ID;
						this.SendASDU (asdu);
					}
						
				} else {
					// connection not activated --> skip message
					if (debugOutput)
						Console.WriteLine ("SLAVE: Connection not activated. Skip I message");
				}


				return true;
			}

			// Check for TESTFR_ACT message
			else if ((buffer [2] & 0x43) == 0x43) {

				if (debugOutput)
					Console.WriteLine ("SLAVE: Send TESTFR_CON");

				socket.Send (TESTFR_CON_MSG);
			} 

			// Check for STARTDT_ACT message
			else if ((buffer [2] & 0x07) == 0x07) {

				if (debugOutput)
					Console.WriteLine ("SLAVE: Send STARTDT_CON");

				this.isActive = true;

				socket.Send (STARTDT_CON_MSG);
			}

			// Check for STOPDT_ACT message
			else if ((buffer [2] & 0x13) == 0x13) {
				
				if (debugOutput)
					Console.WriteLine ("SLAVE: Send STOPDT_CON");

				this.isActive = false;

				socket.Send (STOPDT_CON_MSG);
			} 

			// S-message
			else if (buffer [2] == 0x01) {

				int messageCount = (buffer[4] + buffer[5] * 0x100) / 2;

				if (debugOutput)
					Console.WriteLine ("SLAVE: Recv S(" + messageCount + ") (own sendcounter = " + sendCount + ")");
			}
			else {
				if (debugOutput)
					Console.WriteLine ("SLAVE: Unknown message");
			}

			return true;
		}

		private void checkServerQueue()
		{
			ASDU asdu = server.DequeueASDU ();

			if (asdu != null)
				SendASDU(asdu);
		}

		private bool handleTimeouts() {
			UInt64 currentTime = (UInt64) SystemUtils.currentTimeMillis();

			if (currentTime > nextT3Timeout) {

				if (outStandingTestFRConMessages > 2) {
					if (debugOutput)
						Console.WriteLine ("SLAVE: Timeout for TESTFR_CON message");

					// close connection
					return false;
				} else {
					socket.Send (TESTFR_ACT_MSG);

					if (debugOutput)
						Console.WriteLine ("SLAVE: U message T3 timeout");
					outStandingTestFRConMessages++;
					ResetT3Timeout ();
				}
			}

			if (unconfirmedMessages > 0) {

				if (((long) currentTime - lastConfirmationTime) >= (parameters.T2 * 1000)) {

					lastConfirmationTime = (long) currentTime;
					unconfirmedMessages = 0;
					sendSMessage ();
				}
			}

			return true;
		}

		private void HandleConnection() {

			byte[] bytes = new byte[300];

			try {

				try {

					running = true;

					while (running) {

						try {
							// Receive the response from the remote device.
							int bytesRec = receiveMessage(socket, bytes);
							
							if (bytesRec != 0) {
							
								if (debugOutput)
									Console.WriteLine(
										BitConverter.ToString(bytes, 0, bytesRec));
							
								if (HandleMessage(socket, bytes, bytesRec) == false) {
									/* close connection on error */
									running = false;
								}
							}

							if (isActive)
								checkServerQueue();
												
							if (unconfirmedMessages > parameters.W) {
								lastConfirmationTime = SystemUtils.currentTimeMillis();

								unconfirmedMessages = 0;
								sendSMessage ();
							}
							
							Thread.Sleep(1);
						} catch (SocketException) {
							running = false;
						}

						running = handleTimeouts();
					}

					if (debugOutput)
						Console.WriteLine("SLAVE: CLOSE CONNECTION!");

					// Release the socket.
					socket.Shutdown(SocketShutdown.Both);
					socket.Close();

					if (debugOutput)
						Console.WriteLine("SLAVE: CONNECTION CLOSED!");

				} catch (ArgumentNullException ane) {
					if (debugOutput)
						Console.WriteLine("SLAVE: ArgumentNullException : {0}",ane.ToString());
				} catch (SocketException se) {
					if (debugOutput)
						Console.WriteLine("SLAVE: SocketException : {0}",se.ToString());
				} catch (Exception e) {
					if (debugOutput)
						Console.WriteLine("SLAVE: Unexpected exception : {0}", e.ToString());
				}

			} catch (Exception e) {
				Console.WriteLine( e.ToString());
			}

			server.Remove (this);
		}


		public void Close() 
		{
			running = false;
		}


		public void ASDUReadyToSend () 
		{
			if (isActive) {
				ASDU asdu = server.DequeueASDU ();	

				if (asdu != null)
					SendASDU (asdu);
			}
		}

	}
	
}
