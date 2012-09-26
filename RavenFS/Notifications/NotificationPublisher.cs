﻿using RavenFS.Infrastructure.Connections;

namespace RavenFS.Notifications
{
	public class NotificationPublisher : INotificationPublisher
	{
        private readonly TransportState transportState;

        public NotificationPublisher(TransportState transportState)
        {
            this.transportState = transportState;
        }

        public void Publish(Notification change)
        {
            transportState.Send(change);
        }
    }
}